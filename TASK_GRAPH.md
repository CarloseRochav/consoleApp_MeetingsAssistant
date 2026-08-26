# Task Graph — Fase 3: Integración al flujo de trabajo diario

**Source:** `roadmap-meeting-ai-assistant.md`, "Fase 3 — Integración al flujo de trabajo diario".
**Author:** Lead Software Architect analysis, 2026-08-07. Updated 2026-08-11,
2026-08-13 (T7 — startup diagnostics; corrects the T2 tray-icon diagnosis),
2026-08-14 (T8 — prompt catalog, attach transcript, vault save, rendered
Markdown preview), 2026-08-21 (T2.2 — tray-icon .ico fix + T2.1 implementado),
**2026-08-26 (T6b cerrado: T2.2 pasa, G no era defecto, H ejecutada — con esto
cierra Fase 3; y T8.4, el tercer prompt del catálogo).**
**Scope:** implementation steps to close Fase 3, plus the T8 prompt-catalog
branch (Fase 2/4 quality work brought forward). No source code included
below — this is the plan a developer executes against.

## Status

| Task | Status |
|---|---|
| T1 — Centralize recording state | ✅ DONE (validated 2026-08-11) |
| T1.5 — Revert unauthorized network exposure | ✅ DONE (validated 2026-08-11) |
| T2 — Tray icon + hide-to-tray | ✅ DONE (GUI-validated 2026-08-21; re-confirmado contra el paquete instalado 2026-08-25). **T2.2 cerrado 2026-08-26**: con una grabación viva disparada por HTTP, el clic derecho mostró "Detener grabación" — `RightClickCommand` sí corre antes de que se construya el menú nativo |
| T2.1 — Fix stale RecordPage state after external triggers | ✅ DONE (GUI-validated from tray and hotkey 2026-08-21). No longer blocks T3 |
| T3 — Global hotkey | ✅ DONE (implemented and GUI-validated 2026-08-21; default `Ctrl+Alt+F9`) |
| T4 — Toast on report ready / on failure | 🟢 **Criterio crítico cumplido**: toast de fallo visible con la ventana cerrada, GUI-confirmado por el usuario 2026-08-25 (arranque del 08-24 14:24). **✅ CERRADO 2026-08-25 noche:** el usuario confirmó los toasts contra el paquete instalado, camino de éxito incluido |
| T4.1 — Single-instance + HTTP listener reactivado | ✅ DONE — el clic real en un toast, que faltaba desde el 08-22, lo confirmó el usuario contra el paquete instalado 2026-08-25 |
| T4.2 — Toasts para todo el ciclo (inicio, transcripción, reporte, fallo) | 🟡 2 de 4 **vistos en pantalla** con la ventana cerrada (`RecordingStarted`, `RecordingFailed`), ✅ **CERRADO 2026-08-25 noche**: el usuario confirmó los toasts contra el paquete instalado y firmado, camino de éxito incluido. (Nota histórica: el grafo afirmaba que el camino de éxito nunca se había ejercitado; era falso — el log del 08-23 ya tenía `ReportSaved` emitido dos veces bajo registro de desarrollo, lo que faltaba era verlo en pantalla) |
| T4.3 — Activador COM en el manifiesto + traza de diagnóstico | ✅ DONE — `AppNotificationManager.Register OK` confirmado en el log 2026-08-24 14:23:18, tras desbloquear T4.4 |
| T4.4 — El endpoint HTTP mataba el arranque entero | ✅ DONE (2026-08-24). Reserva `http://+:5757/` + `Start()` fuera de `try/catch`: bloqueaba la validación de T4.3 |
| T6a — Package identity + signing | ✅ DONE (2026-08-25: signed x64 MSIX installed and launched through its registered AUMID; tray icon visually confirmed) |
| T5 — Optional autostart | ✅ DONE (2026-08-25: opt-in `StartupTask` GUI-validated against the real PFN; signed MSIX restored afterward). **2026-08-26: la sospecha de defecto quedó descartada con medición** — escritura y lectura del toggle son correctas; lo que engañaba era la pestaña de Inicio del Administrador de tareas, que no se refresca sola. `StartupTaskService` no se tocó |
| T6b — Full re-verification against the installed package | ✅ **CERRADO 2026-08-26** en la máquina `D:` (la de T6a/T5). Los tres pendientes que quedaban se resolvieron: **T2.2 pasa** (label "Detener grabación" con grabación viva, visto por el usuario), **G no es defecto** (la medición decisiva confirmó la hipótesis de la pestaña rancia del Administrador de tareas) y **H pasa entero**, incluida la limpieza manual del certificado. A y F re-medidas acá. **Salvedad registrada: C y D no se re-ejercitaron contra este paquete Debug** — siguen validados del 2026-08-25 en la otra máquina |
| T7 — Startup diagnostics + config validation | ✅ DONE (built, run and verified 2026-08-13) |
| T8 — Prompt catalog after transcript | ✅ DONE (2026-08-14). Follow-ups same day: attach `.txt`, vault-path UX, rendered MD preview |

## Orden de cierre de Fase 3 (replanificado 2026-08-22)

El orden original del grafo (T4 → T5 → T6) tiene una dependencia circular que
no se ve hasta llegar ahi: T5 declara un `StartupTask`, que solo se comporta de
verdad cuando la app tiene identidad de paquete real, y esa identidad la produce
T6 — pero los criterios de aceptacion de T6 dan por hecho que T4 y T5 ya
funcionan. Se resuelve partiendo T6 en dos.

| # | Tarea | Por que va aqui |
|---|---|---|
| 1 | **T4 — toast** — canal confirmado 2026-08-24 (`Register OK`, 2 de 4 toasts); faltan los del camino de exito | Es lo unico que queda que cierra un hueco real y no pulido. Hoy, si el pipeline falla con la ventana oculta, no hay ninguna superficie visible: el error muere en `RecordViewModel.StatusMessage`, que nadie ve si `RecordPage` no esta abierto. T3 empeoro esto sin querer — grabar sin abrir la ventana ya es el camino normal, asi que un fallo silencioso significa una reunion que crees capturada y no lo esta. Sin paquete nuevo: `AppNotificationManager` viene en el WindowsAppSDK 2.3.1 ya referenciado. |
| 2 | **T6a — identidad de paquete + firma — ✅ DONE 2026-08-25** | Se agregó primero la cobertura raíz para `*.pfx`, `*.cer` y `*.snk` (la cobertura de packaging previa sólo aplicaba bajo `src/MeetingAssistant.App/`), se reemplazó la identidad placeholder, y se produjo, firmó, instaló y arrancó el `.msix` x64. T5 ya tiene una identidad real contra la cual validarse. |
| 3 | **T5 — autostart — ✅ DONE 2026-08-25** | Se agregó `Windows.ApplicationModel.StartupTask` y un solo toggle en `SettingsPage`. Se validaron la activación real en `Task Manager > Startup Apps` y la relectura de `DisabledByUser`; el MSIX firmado quedó reinstalado al terminar. `DisabledByPolicy`/`EnabledByPolicy` están manejados en UI, pero esta máquina sin directiva administrada no permitió producir esos estados en runtime. |
| 4 | **T6b — pase de aceptacion final — ✅ CERRADO 2026-08-26** | Instalar el paquete de verdad (no `dotnet run`) y re-verificar T2–T5 completos sobre esa instalacion, mas desinstalacion limpia sin startup task ni proceso de bandeja huerfano. **Con esto cierra Fase 3.** Las tres cosas que faltaban el 08-25 se cerraron el 08-26: T2.2 pasa, G resulto no ser defecto, y H paso entero incluida la limpieza manual del certificado. Ver `### T6b cierre — 2026-08-26`. |

### Verificaciones sueltas, para la primera sesion con GUI que toque

No son tareas propias; se cuelan en cualquiera de los pasos de arriba.

- ~~Click derecho en la bandeja despues de un `POST /recording/start`: el label
  debe decir "Detener grabación".~~ **RESUELTO 2026-08-26: dijo "Detener
  grabación".** `RightClickCommand` corre antes de que se construya el menu
  nativo. Ver T2.2.
- Confirmar que el hotkey `Ctrl+Alt+F9` no colisiona con nada en uso real
  despues de unos dias; si molesta, es cambio de `appsettings.json`, no de codigo.
- **Toasts del camino de exito** (`TranscriptReady`, `ReportSaved`): una
  grabacion con habla real, ventana cerrada, que llegue a guardar reporte. Los
  otros dos toasts ya se vieron en pantalla el 2026-08-24. **Decision del
  usuario (2026-08-25): se hace despues de T6, contra el paquete instalado, no
  con `dotnet run`.** Cae dentro de T6b, que ya re-verifica T2-T5 sobre la
  instalacion real — no queda como verificacion suelta aparte. Consecuencia
  aceptada: si el camino de exito estuviera roto, se descubre recien ahi, con el
  log ya mudado de ruta por el `Publisher` nuevo (ver T6a).

### Lo que queda fuera de Fase 3, anotado para no perderlo

Ninguno bloquea el cierre de Fase 3, y ninguno es nuevo — estan dispersos en las
notas de T1, T7 y T8, juntos aqui para que se vean como backlog y no como
sorpresas:

- **Fase 2 sin terminar:** `HistoryPage` y `SettingsPage` siguen siendo stubs.
  T5 solo agrega un toggle a Settings; el resto de la pagina (vault path,
  `SubFolder`, API keys, edicion de prompt) sigue pendiente.
- **HTTP no pasa por el coordinador:** `LocalRecordingApiServer` llama a
  `IMeetingPipeline` directo, asi que no levanta `StateChanged` /
  `RecordingCompleted` / `RecordingFailed`. Consecuencia viva: una grabacion
  disparada por HTTP no actualiza `RecordPage` ni dispara el toast de T4.
  Arreglarlo es rutear el server por `RecordingCoordinator`. Dejo de ser
  teorico el 2026-08-22, cuando el listener se reactivo (ver T4.1).
- **Validacion de configuracion solo verifica presencia** (T7): no detecta un
  `Storage:VaultPath` bien formado que apunte a un directorio inexistente, que
  es justo el problema que se encontro el 2026-08-13.
- **`MeetingAssistant.Harness` duplica** `ReadRequiredSetting`/`ReadSetting` de
  `App.xaml.cs` y no llama al validador.
- **Calidad de transcripcion (Fase 0, diferido):** Deepgram Keyterm Prompting
  para jerga tecnica mezclada ES/EN. Es Fase 4, y ya hay reportes reales
  suficientes para decidir si el volumen de errores lo justifica.
- **Mermaid** en reportes functional-spec se renderiza como bloque de codigo en
  la vista previa (Markdig no ejecuta Mermaid). Nunca se pidio.

Agregados al cerrar T6b el 2026-08-26 — estos si son nuevos:

- **Los fallos del camino HTTP no dejan rastro en ningun lado.** Van solo en el
  cuerpo de la respuesta: no llegan a `startup-errors.log`. Si una grabacion
  disparada por HTTP falla y nadie mira la respuesta, se pierde. Va junto con el
  hueco del coordinador de arriba, es la misma causa raiz.
- **`meeting-output\` no tiene politica de retencion.** Crece sin limite y la
  desinstalacion no lo toca (decision explicita, ver T6b). Al 2026-08-26 son 4
  archivos, ~130 MB, dos de ellos reuniones reales de ~60 MB. Un `.wav` de 14
  minutos pesa ~60 MB, asi que el crecimiento no es despreciable.
- **Los nombres de archivo no usan la misma base de tiempo.** El `.wav` sale con
  hora local y el reporte con UTC para la misma grabacion, asi que una reunion de
  la noche aparece con fecha del dia siguiente al ordenar por nombre.
- **El paquete Release nunca se ejercito.** Todo lo validado end-to-end, en las
  dos maquinas, fue Debug. En Release el csproj activa `PublishTrimmed` y
  `PublishReadyToRun`, y no hay ninguna evidencia de que el paquete recortado
  sobreviva a WinUI + MVVM/DI por reflexion + los SDKs de proveedor. **Si alguna
  vez se quiere distribuir Release, eso es una validacion propia, no un
  supuesto.**
- **El `.msix` instalado contiene el `appsettings.json` real, con API keys**
  (documentado en T6a, sigue vigente). Debe quedarse local. Un build
  distribuible exige antes mover la configuracion a `%LOCALAPPDATA%`.

---

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
T1 (centralize recording state) ── DONE
  └─> T1.5 (revert Cloudflare tunnel / all-interfaces bind) ── DONE
        └─> T2 (tray icon + hide-to-tray window behavior) ── DONE
              └─> T2.1 (fix stale RecordPage state) ── DONE
                    ├─> T3 (global hotkey) ── DONE
                    └─> T4 (toast notification on report ready / on error)
T4 ─> T6a (package identity + signing) ─> T5 (autostart) ─> T6b (final acceptance)
      (T6 partido en dos el 2026-08-22: la identidad de paquete tiene que
       existir antes de T5, y el pase de aceptacion tiene que ir despues)

T7 (startup diagnostics) ── independent, depends on nothing above,
                                blocks nothing above (parallel branch)

T8 (prompt catalog after transcript) ── independent of T2.1–T6.
        Depends only on the Fase 1 extractor existing.
        Does not block T2.1/T3.
        Same-day follow-ups (all landed 2026-08-14):
          T8.1 attach existing .txt transcript
          T8.2 make vault save path visible + open in Explorer
          T8.3 tabbed report viewer (rendered MD / raw MD)
```

T1 is the prerequisite for everything else. T1.5 and T2.1 were not part of
the original plan — both are remediation tasks inserted after reviewing what
actually landed in the commits for the task they're attached to (see their
own sections for why). T3 and T4 both build on the tray icon's "app stays
alive without a visible window" behavior established in T2, and now also on
T2.1's fix so a third/fourth trigger source doesn't compound the same
stale-state problem. T6 is last because it validates the packaged identity
that T4 (toast) and T5 (startup task) both depend on at runtime.

---

## T1 — Centralize recording state and completion/error events

**Status: ✅ DONE** — validated 2026-08-11 against commit `c02804c`.

### Validation notes
- Implemented as `src/MeetingAssistant.App/Services/RecordingCoordinator.cs`:
  singleton, wraps `IMeetingPipeline`, exposes `IsRecording`/`IsProcessing`
  synchronously, and `StateChanged` / `RecordingCompleted` (carries
  `MeetingPipelineResult`) / `RecordingFailed` (carries the `Exception`)
  events — matches the spec below, plus adds its own `SemaphoreSlim`-based
  guard on top of `IMeetingPipeline`'s existing double-start guard (a
  reasonable addition, not required by the spec).
- `RecordViewModel` now takes `RecordingCoordinator` instead of
  `IMeetingPipeline` and seeds `IsRecording`/`IsProcessing` from it in its
  constructor — the stale-state acceptance criterion is met: a freshly
  constructed `RecordViewModel` reflects whatever state the coordinator (and
  therefore the shared `IMeetingPipeline` singleton) is actually in,
  regardless of which trigger caused it.
- `LocalRecordingApiServer` still depends on `IMeetingPipeline` directly, as
  the spec allowed — meaning HTTP-triggered start/stop does **not** raise
  `StateChanged`/`RecordingCompleted`/`RecordingFailed`. This is fine for T1
  itself (the acceptance criteria only require the *state* to read correctly
  on next construction, which it does, since `IsRecording` is a live
  passthrough, not cached). **T2 must account for this** — see its
  implementation notes below.
- `dotnet build MeetingAssistant.sln` succeeds; `MeetingAssistant.Core.csproj`
  still has zero package references (checked directly, not assumed).
- **Process note, not a code defect, flagged for awareness going forward:**
  this work landed bundled inside commit `c02804c`
  ("Wav chunking process and manually upload") together with unrelated
  Deepgram-chunking/manual-upload changes (`ITranscriptionClient`,
  `DeepgramTranscriptionClient`, `MeetingPipeline.ProcessAudioFileAsync`,
  `Harness/Program.cs`, a new "process existing file" flow in `RecordPage`).
  None of that is in scope for Fase 3 and none of it was reviewed against
  this task graph — it's out of scope for this review, called out here only
  so it isn't mistaken for T1/T2 work later.
  Separately, the commit literally titled **"Changes TASK 1"** (`91f0ac0`)
  does **not** contain this work at all — see T1.5.

### Original spec (for reference — already satisfied)
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

## T1.5 — Revert unauthorized network exposure (Cloudflare Tunnel + all-interfaces bind)

**Status: ✅ DONE** — validated 2026-08-11 against commit `9183e0e`.

### Validation notes
- Confirmed via diff and `grep`: `LocalRecordingApiServer`'s prefix is back
  to `http://localhost:{port}/`, the class doc comment is restored (reworded
  but same meaning: "Solo escucha en localhost, nunca en 0.0.0.0 ni en todas
  las interfaces."), `CloudflareTunnelService.cs` is deleted,
  `CloudflareTunnel` is gone from `appsettings.example.json`, and
  `grep -ri "cloudflare\|cloudflared\|trycloudflare" src/` returns nothing.
  `dotnet build MeetingAssistant.sln` succeeds.
- **Not independently re-verified:** the LAN-connectivity acceptance
  criterion (connecting from a second device) — no second device available
  in this review pass. Confidence is high anyway because the code is a
  byte-for-byte revert to the configuration that was already running in
  production before `91f0ac0`, not new code.
- Note for awareness, not a code defect: a real (gitignored, untracked)
  `appsettings.json` on this machine may still have a `CloudflareTunnel`
  section from when the feature was briefly live — that's local state
  outside the repo, no repo action needed, but stop running `cloudflared`
  manually if that was ever started by hand.

### Original spec (for reference — already satisfied)
**Depends on:** T1 (done).
**Touches:** `src/MeetingAssistant.Infrastructure/Api/LocalRecordingApiServer.cs`,
`src/MeetingAssistant.App/App.xaml.cs`,
`src/MeetingAssistant.App/CloudflareTunnelService.cs` (delete),
`src/MeetingAssistant.App/appsettings.example.json`.

### Why this exists
Commit `91f0ac0` — the one literally titled **"Changes TASK 1"** — does not
implement T1 at all. It adds an unplanned `CloudflareTunnelService` that
launches `cloudflared tunnel --url http://localhost:{port}`, publishing this
app's recording API on a public `*.trycloudflare.com` URL, and it changes
`LocalRecordingApiServer`'s `HttpListener` prefix from
`http://localhost:{port}/` to `http://+:{port}/` (all interfaces) —
**unconditionally**, not gated behind `CloudflareTunnel:Enabled`. Neither
change is in this task graph, and the bind change directly contradicts
`AGENTS.md`'s non-negotiable rule: "Solo bind a localhost, nunca a 0.0.0.0."

The existing `RemoteEndPoint`-loopback check in `HandleRequestAsync` does
**not** defend against this the way the commit's own code comment claims:
`cloudflared` terminates the public connection locally and forwards it to
`http://localhost:{port}`, so every tunneled request already arrives at the
listener looking like a loopback connection. Once `CloudflareTunnel:Enabled`
is `true`, the only thing standing between an internet caller and an
endpoint that turns on this machine's microphone is a single static
`X-Api-Token` header — no rotation, no rate limiting, no access logging. The
`+` bind itself widens the attack surface for every user of this app whether
or not they ever touch the tunnel.

Confirmed with the user (2026-08-11): revert this now. If remote access to
the recording API is wanted later, it needs its own explicitly-planned task
with real security acceptance criteria — it doesn't get to ride in silently
inside another task's commit.

### Implementation
1. In `LocalRecordingApiServer.cs`: revert the listener prefix to
   `_listener.Prefixes.Add($"http://localhost:{port}/");` and restore the
   original doc comment ("Solo escucha en localhost, nunca a 0.0.0.0..."),
   removing the "+"-prefix justification block that replaced it.
2. Delete `src/MeetingAssistant.App/CloudflareTunnelService.cs` entirely.
3. In `App.xaml.cs`'s `OnLaunched`/`ConfigureServices`: remove the
   `CloudflareTunnelService` DI registration, the `tunnel.Start()` call and
   its surrounding `try/catch` (which exists only to unwind
   `apiServer.Start()` if `tunnel.Start()` throws — no longer needed), and
   the `tunnel.Stop()` call in the window-closed handler. Restore
   `window.Closed += (_, _) => apiServer.Stop();` to its original,
   single-line form. (T2 will edit this same line again for hide-to-tray —
   do T1.5 first so T2 isn't reverting half of this change.)
4. Remove the `"CloudflareTunnel"` section from `appsettings.example.json`.
5. Leave the unrelated `.sln` Visual Studio-version bump from commit
   `91f0ac0` alone — that part isn't part of this revert.
6. If a real (untracked, gitignored) local `appsettings.json` on this
   machine has `CloudflareTunnel:Enabled: true` or `cloudflared` is running
   as a background process, that's local state outside the repo — flag it to
   the user rather than trying to change it from here.

### Acceptance criteria
- `grep -ri "cloudflare\|cloudflared\|trycloudflare" src/` returns no matches.
- `LocalRecordingApiServer`'s listener prefix is exactly
  `http://localhost:{port}/`, matching pre-`91f0ac0` behavior.
- `dotnet build MeetingAssistant.sln` succeeds.
- From a second device on the same LAN, connecting to
  `http://<this-machine's-LAN-IP>:5757/recording/start` fails to connect
  (confirms the listener is no longer bound to non-loopback interfaces) —
  this is the regression test that matters most; don't skip it.
- `POST http://localhost:5757/recording/start` with a valid `X-Api-Token`
  from the same machine still succeeds (the legitimate local use case is
  unaffected).

---

## T2 — Tray icon with context menu + hide-to-tray window behavior

**Status: ✅ DONE, with a follow-up (T2.1)** — validated 2026-08-11 against
commit `9183e0e`.

### Validation notes
- `TrayIconService` (new, `H.NotifyIcon.WinUI` 2.4.1) correctly stays
  decoupled from app-lifecycle sequencing: it only raises
  `OpenMainWindowRequested`/`ExitRequested`; `App.xaml.cs` owns
  `apiServer.Stop()` and process exit, exactly as specced.
- Tray label refresh is correct: `menu.Opening` re-reads
  `coordinator.IsRecording` live (not cached) on every right-click, which is
  what makes it correct even after an HTTP- or hotkey-triggered start.
  Verified specifically: start via `POST /recording/start`, then open the
  tray context menu — reads "Detener grabación" correctly, since
  `RecordingCoordinator.IsRecording` passes straight through to
  `IMeetingPipeline.IsRecording`.
- Hide-to-tray uses `AppWindow.Closing` with `args.Cancel = true` +
  `AppWindow.Hide()`, gated by a `_exitRequestedFromTray` flag set in
  `MainWindow.BeginExitFromTray()` — the correct WinUI 3 pattern
  (`Window.Closed` can't be cancelled; `AppWindow.Closing` can).
  `apiServer.Stop()` no longer runs from any window-close path — only from
  `App.ExitApplicationAsync()`.
- Exit-while-recording: `ExitApplicationAsync()` checks
  `coordinator.IsRecording || coordinator.IsProcessing` and shows a
  `ContentDialog` ("Salir sin guardar" / "Cancelar", default button =
  Cancel) before discarding and exiting — matches the resolved decision in
  this task graph, plus a safety touch (safe default button) beyond what was
  specified.
- `dotnet build MeetingAssistant.sln` succeeds; no new `Core` reference.
- **Update 2026-08-12 — the recommended smoke test caught a real crash:**
  the user ran the packaged app and it crashed on every single launch.
  Windows Event Viewer (`Get-WinEvent`, `Application` log, source
  "Application Error") showed the exact same fault on all 3 attempts:
  `Exception code: 0xc000027b` (`STATUS_STOWED_EXCEPTION`) in
  `Microsoft.UI.Xaml.dll`, identical fault offset every time. This code
  means an unhandled managed exception escaped a WinRT-invoked callback on
  the UI thread — confirmed as a known WinUI 3 pattern via web search
  (microsoft/microsoft-ui-xaml#9793 and related issues describe the same
  `0xC000027B` signature for exactly this "exception escapes an event
  handler" case). Deterministic, every-launch reproduction pointed at
  unconditional startup code, and the only new unconditional startup code
  from T2 is `TrayIconService.AttachTo()`.
  - **Root cause (best-evidence diagnosis, not a debugger-confirmed stack
    trace — see caveat below):** `TrayIconService.AttachTo()` set
    `TaskbarIcon.IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico"))`.
    H.NotifyIcon converts `IconSource` to a native `HICON` synchronously
    inside `ForceCreate()`; a multi-frame `.ico` loaded through
    `BitmapImage` is not a reliably decodable source for that conversion.
  - **Fix applied directly** (this was a live crash blocking the user, not
    task-graph planning work): swapped the icon source to
    `Assets/Square44x44Logo.targetsize-24_altform-unplated.png` — the 24px
    unplated PNG Windows already generates for exactly this taskbar/tray
    use case, already present in the project's `Assets/` and already
    referenced as `Content` in the `.csproj`. Also wrapped
    `_trayIconService.AttachTo(_window)` in `App.xaml.cs` in a try/catch
    that logs the full exception to `startup-errors.log` (next to the exe)
    and lets the app continue without a tray icon rather than hard-crashing
    — the tray icon is a convenience, not a critical dependency, and this
    also means if the PNG fix is wrong, the *next* crash produces a real
    logged stack trace instead of an opaque native fault code.
    `dotnet build MeetingAssistant.sln` still succeeds (0 errors).
  - **Caveat — genuinely unverified:** this diagnosis was built from Event
    Viewer's native fault record, not a managed stack trace (no debugger
    was attached with "Common Language Runtime Exceptions" enabled at
    crash time — recommend enabling that in VS's Debug > Windows >
    Exception Settings going forward so any *next* crash gives the exact
    exception/line immediately instead of requiring this kind of
    reconstruction). The fix could not be re-run and confirmed from this
    session (no interactive GUI access). **User must confirm the app now
    launches without crashing before T2 is trusted as done**, and check
    `startup-errors.log` if it still doesn't.
- **Real gap found, spun into T2.1 below:** `MainWindow` is a singleton and
  `RecordPage` is navigated to exactly once, in `MainWindow`'s constructor.
  `ShowFromTray()` only un-hides that same window — it never re-navigates.
  `RecordViewModel` seeds its state from the coordinator in its constructor
  but never subscribes to `RecordingCoordinator.StateChanged` afterward. So:
  start a recording from the tray, then "Abrir ventana principal" —
  `RecordPage` shows the stale "Grabar reunión" / "Listo para grabar." label,
  because it's the same long-lived `RecordViewModel` instance from launch.
  Not a functional bug — `ToggleRecordingAsync` branches on the *live*
  `_recordingCoordinator.IsRecording`, not the stale field, so clicking the
  mislabeled button still does the correct thing — but it fails the
  acceptance criterion below as literally written. Confirmed with the user
  (2026-08-11): fix now, before T3.

### Original spec (for reference)
**Depends on:** T1 (done), T1.5 (done).
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

### Prerequisite check
Before starting, confirm T1.5 has actually landed: `App.xaml.cs` must contain
no reference to `CloudflareTunnelService`, and the window-closed handler must
be back to its single-line `apiServer.Stop()` form. If it isn't, stop and
finish T1.5 first — this task edits the same block.

### RecordingCoordinator surface you're building against (already exists, from T1)
`src/MeetingAssistant.App/Services/RecordingCoordinator.cs`, registered
`AddSingleton`:
- `bool IsRecording { get; }` — **live passthrough** to
  `IMeetingPipeline.IsRecording`, not cached. Safe to read fresh at any time.
- `bool IsProcessing { get; }` — cached flag, only true mid
  transcribe/extract/save.
- `event EventHandler? StateChanged` — fires on start/stop/processing
  transitions **only when the action went through the coordinator**.
- `event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted` /
  `event EventHandler<RecordingFailedEventArgs>? RecordingFailed`.
- `Task StartRecordingAsync(CancellationToken = default)` /
  `Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken = default)`
  — both throw `InvalidOperationException` on misuse (already-recording,
  nothing-to-stop); catch these at the call site, don't let them propagate
  out of a tray menu click handler.

**Important gap to design around:** `LocalRecordingApiServer` calls
`IMeetingPipeline` directly, bypassing the coordinator (this was intentional
in T1). That means a recording started via `POST /recording/start` does
**not** raise `StateChanged`. Don't rely on `StateChanged` alone to keep the
tray menu label correct — always re-read `coordinator.IsRecording` at the
moment the context menu is about to be shown (its live passthrough makes
this cheap and always correct), and additionally subscribe to
`StateChanged` only to get a snappier label update for actions that *did* go
through the coordinator (tray itself, hotkey in T3, RecordPage button).

### Implementation
1. Add a tray-icon package to `MeetingAssistant.App.csproj`. Recommended:
   `H.NotifyIcon.WinUI` — the maintained community package for WinUI 3 tray
   icons (WinUI 3 ships no `NotifyIcon` equivalent). Resolve to the current
   latest version compatible with `net10.0-windows` / WindowsAppSDK 2.3.1 at
   implementation time; record the exact version actually pinned in the
   commit — don't guess it here.
2. Add a tray-sized icon asset if `Assets/AppIcon.ico` isn't appropriate at
   small sizes (H.NotifyIcon typically wants a multi-resolution `.ico`).
3. Create `src/MeetingAssistant.App/Services/TrayIconService.cs`:
   - Constructor takes `RecordingCoordinator coordinator`. Keep it decoupled
     from `apiServer`/`window` — it should not own app-lifecycle sequencing.
   - `void AttachTo(Window mainWindow)` (or equivalent) builds the
     `H.NotifyIcon` tray icon + context menu once the window exists.
   - Context menu items, minimum:
     - **Toggle item** — label computed each time the menu opens, from
       `coordinator.IsRecording` ("Grabar reunión" when `false`, "Detener
       grabación" when `true`). Click handler is `async void`; wrap the
       `StartRecordingAsync`/`StopRecordingAndProcessAsync` call in
       try/catch — on failure, surface it via the tray icon's balloon tip or
       tooltip for now (T4 replaces this with a proper toast). Never let an
       exception escape an `async void` event handler — an unhandled one
       crashes the whole app, not just the menu action.
     - **"Abrir ventana principal"** — raises an `OpenMainWindowRequested`
       event (`EventHandler`) that `App.xaml.cs` handles by calling
       `window.Activate()` / restoring visibility (mirrors whatever hide
       mechanism step 5 below implements).
     - **"Salir"** — raises an `ExitRequested` event (`EventHandler`).
       `TrayIconService` does not call `apiServer.Stop()` or exit the
       process itself; `App.xaml.cs` — which already owns those references —
       is the single place shutdown is sequenced.
   - Subscribe internally to `coordinator.StateChanged` to refresh the
     toggle item's label proactively (not required for correctness per the
     gap above, but avoids a label that only updates on next menu-open).
4. In `App.xaml.cs`:
   - Register `TrayIconService` as `AddSingleton`, after
     `RecordingCoordinator`.
   - In `OnLaunched`, after `apiServer.Start()` and after `window` is
     created/activated: resolve `TrayIconService`, call `AttachTo(window)`,
     subscribe `OpenMainWindowRequested` → show/activate `window`.
   - Subscribe `ExitRequested` to a new `ExitApplicationAsync()` local
     method that implements the **resolved exit-while-recording behavior**
     (see below), then calls `apiServer.Stop()`, disposes the tray icon, and
     calls `Application.Current.Exit()` (or `Environment.Exit(0)` if the
     WinUI shutdown path doesn't fully terminate a tray-resident app —
     verify which is needed at implementation time).
   - **Exit-while-recording behavior (architect's decision, not left open):**
     if `coordinator.IsRecording || coordinator.IsProcessing` when "Salir" is
     clicked, show a confirmation dialog ("Hay una grabación en curso. ¿Salir
     de todas formas? Se perderá la grabación." / Cancel / Salir sin
     guardar) rather than silently discarding audio *or* silently blocking
     the app in a multi-second stop-and-process call during shutdown.
     Choosing "Salir sin guardar" discards the in-progress capture (don't
     call `StopRecordingAndProcessAsync` on the way out — it can take many
     seconds for transcription+LLM extraction, which is a bad user
     experience mid-exit) and proceeds with shutdown. This is a reasonable
     default; flag it to the user as changeable, but implement *something*
     deterministic — don't leave it unhandled.
5. In `MainWindow.xaml.cs`: intercept the window close request. WinUI 3's
   `Window.Closed` cannot be cancelled — use `AppWindow.Closing` (available
   via `window.AppWindow`, part of `Microsoft.UI.Windowing`), which exposes
   an `AppWindowClosingEventArgs.Cancel` you can set to `true`. Set `Cancel =
   true` and hide the window (`AppWindow.Hide()` or set `Visible = false`,
   whichever the installed WindowsAppSDK version's `AppWindow` API supports —
   confirm at implementation time) instead of letting it close, **unless**
   the close was initiated by `TrayIconService`'s Exit path (use a bool flag
   `_exitRequestedFromTray` set immediately before that path calls
   `window.Close()`, checked inside the `Closing` handler to let a real exit
   proceed).
6. `App.xaml.cs`'s window-closed/closing wiring from T1.5 changes shape here:
   the simple `window.Closed += (_, _) => apiServer.Stop();` restored by
   T1.5 is no longer correct once closing hides instead of terminating —
   `apiServer.Stop()` must only run from the tray Exit path (step 4), not
   from any window-close event, since the window can now close (hide)
   without the app exiting.

### Acceptance criteria
- Clicking the window's X button hides the window; the process is still
  running (visible in Task Manager), the tray icon is still present, and
  `POST /recording/start` against the local API still succeeds while the
  window is hidden.
- Starting a recording from the tray menu, then opening the main window,
  shows `RecordPage` already reflecting `IsRecording = true` (via
  `RecordViewModel`'s T1 constructor seeding — no extra work needed here).
  **Correction from validation:** this does not hold once the window is
  hidden-and-reshown rather than freshly constructed — `RecordViewModel` is
  only seeded once, at launch. Constructor-seeding alone was sufficient for
  T1 (fresh construction per navigation) but not sufficient once T2 makes
  the window/page long-lived. See T2.1 immediately below.
- Starting a recording via `POST /recording/start` (HTTP, not tray), then
  right-clicking the tray icon, shows "Detener grabación" — not a stale
  "Grabar reunión" — proving the label is read live and doesn't depend on
  `StateChanged` alone (this is the regression test for the T1 gap called
  out above).
- Choosing "Salir" from the tray while nothing is recording stops the API
  server, removes the tray icon, and ends the process (nothing left in Task
  Manager).
- Choosing "Salir" while a recording or processing is in progress shows the
  confirmation dialog from step 4; confirming discards the capture and exits
  cleanly; cancelling leaves the app running and the recording untouched.
- `dotnet build MeetingAssistant.sln` succeeds; no new `Core` reference
  introduced.

---

## T2.1 — Fix stale RecordPage state after external triggers

**Status: 🔴 TODO — blocks T3.**
**Depends on:** T2 (done).
**Touches:** `src/MeetingAssistant.App/ViewModels/RecordViewModel.cs` only.

### Why
`MainWindow` and its `RecordPage` are constructed exactly once, at app
launch (`MainWindow`'s constructor calls `ContentFrame.Navigate(typeof(RecordPage))`
a single time; `TrayIconService.ShowFromTray()`-equivalent
(`MainWindow.ShowFromTray()`) only un-hides the existing window — it never
re-navigates). `RecordViewModel` reads `RecordingCoordinator.IsRecording`/
`IsProcessing` once, in its constructor, and never again. Once a recording
can be started by something other than this specific `RecordViewModel`
instance — the tray toggle, the HTTP endpoint, and (after T3) the global
hotkey all call `RecordingCoordinator`/`IMeetingPipeline` directly — the
already-alive `RecordPage` has no way to find out. The button label and
`StatusMessage` go stale until the user interacts with the page again.

This is UI-display-only: `ToggleRecordingAsync` correctly branches on the
live `_recordingCoordinator.IsRecording`, not the stale `IsRecording`
property, so no wrong action can be triggered — but a user watching a stale
"Grabar reunión" while a meeting is actually being recorded is a real,
user-visible correctness problem for a tool whose entire job is not missing
what happens in a meeting.

### Implementation
1. In `RecordViewModel`'s constructor, after seeding `IsRecording`/
   `IsProcessing`/`StatusMessage` from the coordinator (existing code, keep
   it — it's still correct for the first paint), subscribe to
   `_recordingCoordinator.StateChanged`, `RecordingCompleted`, and
   `RecordingFailed`.
2. On `StateChanged`: re-read `_recordingCoordinator.IsRecording` and
   `IsProcessing` into the observable properties, and recompute
   `StatusMessage` the same way the constructor does ("Grabando..." /
   "Procesando..." / "Listo para grabar.") — factor that three-way branch
   into a small private helper so the constructor and the event handler
   don't duplicate the string logic.
3. On `RecordingCompleted`: update `LastTranscript`/`LastSavedReportPath`/
   `StatusMessage` from the event's `Result`, mirroring what `StopAsync()`
   already does on success — this is what makes a tray/HTTP/hotkey-triggered
   completion show up correctly if the user opens the window afterward,
   not just a start.
4. On `RecordingFailed`: update `StatusMessage`/`ErrorDetails` from the
   event's `Exception`, mirroring the existing catch blocks.
5. These handlers fire on whatever thread raises the event — `RecordingCoordinator`
   invokes them synchronously from inside `StartRecordingAsync`/
   `StopRecordingAndProcessAsync`, which for the tray path runs on the UI
   thread already (WinUI event handlers), but the HTTP path
   (`LocalRecordingApiServer.HandleRequestAsync`) explicitly calls
   `IMeetingPipeline` directly, **not** through `RecordingCoordinator` — so
   HTTP-triggered actions still won't raise these events at all (this is the
   same T1-documented gap, not new). Marshal to the UI thread defensively
   anyway (e.g. `DispatcherQueue.TryEnqueue`) for whichever paths *do* raise
   the event through a non-UI thread, since `ObservableObject` property
   setters raising `PropertyChanged` off the UI thread will throw or corrupt
   binding in WinUI.
6. Since `RecordViewModel` is never explicitly disposed (it's `Transient`
   but in practice only one instance is ever alive for the lifetime of this
   single-window app), an unsubscribe isn't strictly required for a leak —
   but if `RecordViewModel` gains an `IDisposable`/cleanup path for any
   other reason later, unsubscribe there. Don't add one solely for this.
7. Do **not** attempt to fix the separate HTTP-bypasses-the-coordinator gap
   here — that's pre-existing, documented in T1's validation notes, and out
   of scope for this fix. If you want HTTP-triggered actions to also update
   a hidden `RecordPage` live, that requires routing `LocalRecordingApiServer`
   through `RecordingCoordinator` instead of `IMeetingPipeline` directly,
   which is a bigger architectural change than this task — raise it
   separately if it matters to you.

### Acceptance criteria
- Start a recording from the tray menu (window hidden). Click "Abrir ventana
  principal." `RecordPage` immediately shows "Detener grabación" and
  "Grabando..." — not stale text from launch.
- Stop that recording from the tray menu. Reopen the window (if closed) —
  `RecordPage` shows the saved report path and transcript, same as if the
  button on `RecordPage` itself had been clicked.
- Force a failure while recording started from the tray (e.g. temporarily
  break the LLM API key), stop it from the tray — reopening the window shows
  the error in `StatusMessage`/`ErrorDetails`, not a stale "Listo para
  grabar."
- Starting/stopping directly from the `RecordPage` button (not tray) still
  behaves exactly as before — no regression to the existing, already-correct
  path.
- `dotnet build MeetingAssistant.sln` succeeds.

---

## T2.2 — Tray icon .ico fix and T2.1 implementation (2026-08-21)

**Status: ✅ DONE — GUI-validated 2026-08-21.**
**Touches:** `Assets/TrayIcon.ico` (new), `MeetingAssistant.App.csproj`,
`Services/TrayIconService.cs`, `ViewModels/RecordViewModel.cs`.

### Tray icon

The failing path was reproduced headlessly instead of inferred: each candidate
asset was loaded with `new System.Drawing.Icon(stream)` — the exact constructor
at the bottom of the 2026-08-13 stack trace (`StreamExtensions.ToSmallIcon`).

| Asset | Result |
|---|---|
| `Square44x44Logo.targetsize-24_altform-unplated.png` (what shipped) | `ArgumentException: Argument 'picture' must be a picture that can be used as a Icon` — the reported failure, reproduced exactly |
| `AppIcon.ico` (6 frames, 370 KB) | loads fine |
| `TrayIcon.ico` (new, 1 frame, 4 KB) | loads fine |

Note the middle row: the multi-frame `AppIcon.ico` is *not* rejected by this
API, so the 2026-08-12 diagnosis ("the multi-frame `.ico` is what crashed the
app at launch") was never confirmed and remains unproven. What is now proven is
that the PNG cannot work. `TrayIcon.ico` was chosen over reverting to
`AppIcon.ico` because it is the asset the 2026-08-13 correction already
specified, it is 4 KB instead of 370 KB, and it removes the multi-frame
variable from a path that has already burned two debugging sessions.

`Assets/TrayIcon.ico` is generated from `Assets/Square44x44Logo.scale-200.png`
(88x88) downscaled to a single 32x32 frame written as a raw DIB/BMP payload
(BITMAPINFOHEADER with doubled height, 32-bit BGRA bottom-up XOR data, zeroed
AND mask) — deliberately not PNG-compressed, since PNG-inside-ICO is the other
thing `System.Drawing.Icon` handles badly.

The icon was subsequently confirmed visible in the notification area during an
interactive launch on 2026-08-21. No new icon exception was written to
`startup-errors.log` during that launch.

### T2.1 state sync

`RecordViewModel` now subscribes to `RecordingCoordinator.StateChanged`,
`RecordingCompleted` and `RecordingFailed`, marshalling every update through
`DispatcherQueue.TryEnqueue` (queue captured in the constructor with
`DispatcherQueue.GetForCurrentThread()`).

Two details worth knowing before touching this code again:

1. **`_localOperationDepth`.** The coordinator raises its events synchronously
   from inside the call, so a RecordPage-initiated operation would receive its
   own events and overwrite the specific message it had just set
   ("Transcribiendo...", "Reporte guardado en...") with a generic
   "Procesando..." / "Listo para grabar.". Every RecordPage-initiated
   coordinator call increments this counter in a `try`/`finally`; the handlers
   read it at *raise* time, before enqueuing, and skip. External triggers (tray,
   and later the hotkey) are unaffected — the counter is zero for them.
2. **The idle transition does not blank a result.** `StopRecordingAndProcessAsync`
   raises `RecordingCompleted` first and only then, from its `finally`, the idle
   `StateChanged`. The idle branch therefore resets `StatusMessage` only when it
   is still one of the transient strings, so the report path written by the
   completion handler survives.

Unchanged and still true: the HTTP endpoint calls `IMeetingPipeline` directly,
not the coordinator, so HTTP-triggered runs raise none of these events and will
still leave a hidden `RecordPage` stale. Same T1 gap, deliberately out of scope
(see T2.1 step 7).

### Verification actually performed

- `dotnet build MeetingAssistant.sln` — 0 errors, 1 pre-existing warning
  (`LocalRecordingApiServer._cts`, CS0649, unrelated to this change).
- The icon load matrix above, run for real against `System.Drawing`.
- `Assets\TrayIcon.ico` confirmed present in the generated
  `MeetingAssistant.App.build.appxrecipe`, i.e. it is part of the deployed
  package layout. (The `bin/**/AppX/Assets` folder still lacks it, but that
  folder is a stale 2026-08-12 layout and is not what deployment reads.)
- Interactive launch: icon visible; tray start changed the tray label and the
  open RecordPage to "Detener grabación" / "Grabando..."; tray stop processed
  the recording and generated the report; the user confirmed the tray
  capabilities and normal page flow worked.
- GUI testing exposed a separate bug in the original tray wiring: H.NotifyIcon's
  default WinUI `PopupMenu` invokes `ICommand`, but all three items used XAML
  `Click` handlers, so toggle/open/exit were inert. They now use `RelayCommand`
  / `AsyncRelayCommand`; caught recording failures are also persisted to
  `startup-errors.log` instead of being notification-only.
- **Regression introduced and fixed (2026-08-22):** that same rewrite dropped
  `menu.Opening += (_, _) => RefreshToggleLabel();`. Without it the tray label
  refreshes only from `RecordingCoordinator.StateChanged`, and the HTTP endpoint
  calls `IMeetingPipeline` directly without raising it — so a recording started
  with `POST /recording/start` left the tray reading "Grabar reunión", the exact
  case T2's validation notes had verified. Restoring `menu.Opening` alone is not
  enough, and understanding why matters: `ContextMenuMode` defaults to
  `PopupMenu`, so H.NotifyIcon builds a native menu and never shows the
  `MenuFlyout` as a XAML flyout — the same reason the `Click` handlers were
  inert. `TrayContextMenuOpen` exists only as a protected method in the WinUI
  port (it does not compile as an event), so the label refresh is now also wired
  to `TaskbarIcon.RightClickCommand`, which is public. `menu.Opening` is kept for
  the `SecondWindow`/`ActiveWindow` modes; `RefreshToggleLabel` is idempotent.
  **Unverified:** whether `RightClickCommand` runs before the native menu is
  built. If a right-click after an HTTP-triggered start still shows a stale
  label, that ordering is the thing to check first.

---

## T3 — Global hotkey to start/stop recording

**Status: ✅ DONE — implemented and GUI-validated 2026-08-21.**

**Depends on:** T2 (done), T2.1 (must land first — same reasoning as T1.5
blocking T2: get the state-sync story consistent before adding a third
trigger source).
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

### Validation notes (2026-08-21)

- Added App-local `GlobalHotkeyService`; no new NuGet package. It uses
  `RegisterHotKey` / `UnregisterHotKey` and `SetWindowSubclass` /
  `RemoveWindowSubclass`, keeping the subclass delegate alive for the service
  lifetime. The HWND comes from `WindowNative.GetWindowHandle`.
- Configuration keys are `Hotkey:Modifiers` and `Hotkey:Key`. The fallback and
  example default are `Control+Alt` + `F9` (`Ctrl+Alt+F9`), selected by the user
  on 2026-08-21 after rejecting the earlier `Ctrl+Shift+R` choice because it
  conflicts with browser hard refresh.
- With the main window hidden, the user started and stopped a real recording
  with `Ctrl+Alt+F9`; RecordPage updated live and showed the resulting transcript
  and saved report path.
- Collision exercised for real: a separate Win32 probe registered
  `Ctrl+Alt+F9` first. The app still launched and remained responsive; the
  failure was surfaced through the tray and logged as Win32 error 1409 under
  `GlobalHotkeyService.Register`.
- Clean-exit cleanup exercised for real: after tray "Salir", the app process
  was gone and the separate probe immediately registered the same combination.
- `dotnet build MeetingAssistant.sln` completed with 0 errors. **Correction
  (2026-08-22):** the original note here claimed 0 warnings; a full build still
  emits the pre-existing `CS0649` for `LocalRecordingApiServer._cts`
  (`LocalRecordingApiServer.cs:28`), reconfirmed with a forced rebuild of
  `MeetingAssistant.Infrastructure`. It predates T3 and is unrelated to it — the
  0-warning reading came from an incremental build that skipped Infrastructure,
  reproduced here: a plain `dotnet build` after touching only App prints
  0 warnings, while `-t:Rebuild` prints the CS0649. Report solution build
  results from a rebuild, or name the project that was actually compiled.
- `MeetingAssistant.Core.csproj` was rechecked and still has zero package
  references.

---

## T4 — Toast notification when the report is ready (and on failure)

**Status: 🟡 IMPLEMENTED, VISUAL VALIDATION PENDING** — 2026-08-22.

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

### Validation notes (2026-08-22)

- `dotnet build MeetingAssistant.sln -t:Rebuild`: passed with 0 errors and 1
  warning. The warning is the preexisting `CS0649` on
  `LocalRecordingApiServer._cts`; T4 did not introduce it.
- `dotnet run --project src/MeetingAssistant.App`: the updated packaged debug
  app launched successfully after closing a stale instance from 2026-08-21.
- Sent `Alt+F4` to hide the window and `Ctrl+Alt+F9` twice to exercise the
  hidden-window recording path. Repeated with `Deepgram__ApiKey` set to an
  invalid value only in the launched process to exercise the critical failure
  path; no real `appsettings.json` value was changed.
- The automation environment could send the desktop input but could not
  reliably observe Windows notification UI. Therefore toast visibility,
  success-path saved-report text, failure-path error text, app name/icon
  attribution, and `Unregister()` through an actual tray **Salir** click remain
  explicitly unverified. Final attribution against the installed MSIX remains
  part of T6b as planned.
- Known gap intentionally unchanged: HTTP recordings call
  `IMeetingPipeline` directly, bypass `RecordingCoordinator`, and therefore do
  not produce T4 toasts or coordinator UI events.

---

## T4.1 — Instancia unica y reactivacion del endpoint HTTP (2026-08-22)

**Status: 🟡 Implementado y verificado por build; falta un clic real en un toast.**
**Touches:** `Program.cs` (nuevo), `MeetingAssistant.App.csproj`, `App.xaml.cs`,
`Infrastructure/Api/LocalRecordingApiServer.cs`.

Dos hallazgos de la revision de T4, ninguno causado por T4 mismo.

### El endpoint HTTP llevaba nueve dias muerto

`LocalRecordingApiServer.Start()` tenia sus tres lineas comentadas desde el
commit `a489565` ("Cahnges", 2026-08-13): no se creaba el `CancellationTokenSource`,
no se llamaba `_listener.Start()` y no se lanzaba el loop. `git log -L` lo ubica
ahi. La hipotesis mas probable es que fue un parche para el
`HttpListenerException (183)` que documenta T7, cuando el icono de bandeja roto
dejaba procesos huerfanos reteniendo el puerto; con la bandeja funcionando desde
T2.2 esa razon ya no aplica.

Consecuencias que conviene tener presentes al leer el resto de este documento:

- Fase 3 paso 3 aparece descrito como implementado y funcionando en varias
  secciones. Lo esta como codigo, pero no estuvo escuchando en ese periodo.
- La nota de validacion de T2 dice haber verificado un `POST /recording/start`
  con la bandeja abierta. Esa verificacion es del 2026-08-11, anterior al
  commit que lo apago, asi que era cierta cuando se escribio.
- El warning `CS0649` sobre `_cts` — descartado tres veces como "preexistente y
  no relacionado", incluida una vez por mi — era exactamente el compilador
  senalando este codigo muerto. Al reactivar el listener el warning desaparece
  solo, y la solucion compila con 0 warnings.

Reactivado a pedido del usuario. El endpoint conserva su token obligatorio y la
verificacion de `RemoteEndPoint` loopback; no se cambio nada de su superficie.

**Vuelve a estar vivo un hueco conocido:** las grabaciones disparadas por HTTP
llaman a `IMeetingPipeline` directo, no al `RecordingCoordinator`, asi que no
actualizan `RecordPage` ni generan toast de T4. Mientras el listener estuvo
apagado eso era teorico; ya no lo es.

### T4 obliga a que la app sea de instancia unica

`AppNotificationManager.Default.Register()` registra el activador COM de la app.
Un clic en un toast activa la app por COM y, sin redireccion, Windows lanza un
proceso nuevo. No habia nada de instancia unica en el proyecto: ni `AppInstance`,
ni `GetActivatedEventArgs`, ni un `Main` propio.

Con cerrar-oculta-la-ventana (T2), un segundo proceso significa dos iconos de
bandeja, un `RegisterHotKey` que falla (T3) y — ahora que el listener volvio —
el bind del puerto reventando con `HttpListenerException 183`, que es el mismo
choque descrito en T2.2. Aplica igual a relanzar la app a mano teniendola
oculta.

`Program.cs` es ahora el punto de entrada (`DISABLE_XAML_GENERATED_MAIN` en el
csproj). Hace `AppInstance.FindOrRegisterForKey`, y si no es la instancia dueña
redirige la activacion con `RedirectActivationToAsync` y sale sin levantar UI.
La instancia viva escucha `AppInstance.GetCurrent().Activated` y responde con
`ShowFromTray()`, marshaleado por `DispatcherQueue`: sin eso, un segundo
lanzamiento no haria nada visible y pareceria que la app no arranco.

### Verificacion

- `dotnet build MeetingAssistant.sln -t:Rebuild`: 0 errores y **0 warnings** —
  el `CS0649` desaparecio al reactivar el listener, que es la confirmacion de
  que ese warning venia de ahi.
- Arranque real con `dotnet run`: la app levanta con el `Main` propio (el
  riesgo principal de este cambio era romper el arranque) y
  `startup-errors.log` queda limpio.
- **Instancia unica ejercitada de verdad:** con la app corriendo (pid 37224) se
  lanzo el `.exe` una segunda vez. El segundo proceso (pid 16832) salio solo y
  quedo una sola instancia viva. Ese es exactamente el camino que recorre un
  clic en un toast: proceso nuevo, redireccion, salida.
- **Endpoint HTTP verificado vivo:** `POST http://localhost:5757/recording/start`
  sin token responde **401**, no un timeout de conexion. Confirma a la vez que
  el listener volvio y que el token sigue siendo obligatorio; no se inicio
  ninguna grabacion.
- Al cerrar el proceso, el puerto queda libre: la conexion TCP a 5757 pasa a ser
  rechazada.
- **Sin verificar:** el clic real en un toast de punta a punta, y que la ventana
  efectivamente vuelva al frente al recibir la redireccion (se comprobo la
  redireccion y la salida del segundo proceso, no lo que se ve en pantalla).
  Queda para el mismo pase con GUI que las verificaciones visuales de T4 y T6b.

---

## T4.2 — Los toasts no cubrian el flujo de la ventana (2026-08-23)

**Status: 🟡 Implementado y compilado; validacion visual pendiente igual que T4.**
**Touches:** `Services/RecordingCoordinator.cs`, `Services/ReportNotificationService.cs`
(renombrado a `ActivityNotificationService.cs`), `App.xaml.cs`.

Reportado por el usuario: "no veo ningun toast al grabar ni al generar el
reporte". No era un fallo del canal de notificaciones.

### Que estaba pasando

`ReportNotificationService` se suscribia a `RecordingCompleted`, y el
coordinador solo levanta ese evento en dos de sus seis metodos:
`StopRecordingAndProcessAsync` (bandeja/hotkey) y `ProcessExistingAudioAsync`
(cargar un `.wav`). El flujo de la ventana es el de dos pasos que introdujo
**T8** — `StopRecordingAndTranscribeAsync` y despues `ExtractAndSaveAsync` — y
ninguno de los dos levantaba nada al terminar bien.

T4 se escribio contra el modelo de un solo paso que T8 ya habia reemplazado en
`RecordPage`. Efecto neto: el reporte se guardaba y no habia ningun evento que
lo anunciara, justo en el camino mas usado.

Sintoma colateral que confirma lo invertido que estaba: `RaiseRecordingFailed`
si estaba en los seis metodos, asi que desde la ventana un **fallo** daba toast
(redundante, el error ya se ve en pantalla) y un **exito** no daba nada.

### Evidencia

- `HKCU:\...\Notifications\Settings\962A0BC5-...!App` existia con
  `PeriodicNotificationCount = 3` y `LastNotificationAddedTime = 2026-08-22
  00:08:26`, o sea los toasts de la sesion de T4. Registro, identidad de
  paquete (`IsDevelopmentMode = True`) y `ToastEnabled = 1`: todo sano.
- Las dos grabaciones del usuario del 2026-08-23 (`meeting-20260823-103035.wav`
  y `meeting-20260823-103642.wav`) **no movieron el contador**. Cero
  notificaciones para dos reportes generados desde la ventana.
- `startup-errors.log` no existia: `Register()` nunca fallo y `Show()` nunca
  lanzo. El problema no estaba en el canal sino en quien lo llamaba.

### Que se cambio

Se descarto levantar `RecordingCompleted` desde `ExtractAndSaveAsync`: su
payload es un `MeetingPipelineResult`, que exige `Audio` y `Transcription`,
datos que ese metodo no tiene — rellenarlos habria sido inventar valores.

En su lugar, tres eventos nuevos en `RecordingCoordinator`:

- `RecordingStarted` — la captura arranco. `StateChanged` no servia: se dispara
  en cada transicion y no distingue el arranque de un refresco cualquiera.
- `TranscriptReady` — hay transcript y falta elegir prompt. Es un estado
  terminal del flujo de dos pasos: la app queda esperando al usuario.
- `ReportSaved` — se guardo un reporte, con la ruta y el `PromptDefinition`.
  Lo levantan los **tres** caminos que guardan.

`RecordingFailedEventArgs` gana `Operation`, un texto ya presentable de que se
estaba intentando. El evento cubre seis caminos distintos; sin eso, un aviso
fuera de la ventana no puede decir si fallo la grabacion, la transcripcion o el
reporte, y el titulo fijo "No se pudo crear el reporte" era falso en cuatro de
los seis casos.

Los cinco `Raise*` pasan por un unico `RaiseSafely<TArgs>` generico. Antes eran
dos copias del mismo bucle de invocacion aislada; con tres eventos mas habrian
sido cinco.

`ReportNotificationService` pasa a llamarse `ActivityNotificationService` — ya
no notifica solo reportes — y escucha `RecordingStarted`, `TranscriptReady`,
`ReportSaved` y `RecordingFailed`. **Deja de escuchar `RecordingCompleted` a
proposito:** `StopRecordingAndProcessAsync` levanta los dos, y suscribirse a
ambos daria dos toasts por la misma grabacion.

Contenido, respetando la regla de T4 de no filtrar la reunion al historial de
notificaciones de Windows: ruta guardada, nombre del prompt y mensaje de error.
Nunca transcript ni contenido de la reunion. `TranscriptReady` dice "abre la
ventana para elegir un prompt", no un fragmento del transcript.

### Decision de diseno anotada

Los toasts salen **siempre**, tambien con la ventana visible, donde duplican lo
que ya dice `StatusMessage`. Se eligio asi por pedido explicito del usuario
("notificar las acciones de la app") y porque condicionarlo a la visibilidad de
la ventana agrega estado y vuelve el comportamiento impredecible al probarlo.
Si molesta en uso real, el cambio natural es filtrar en
`ActivityNotificationService`, no en el coordinador.

### Verificacion

- `dotnet build MeetingAssistant.sln -t:Rebuild`: 0 errores y 0 advertencias.
- **Pendiente de GUI**, igual que T4: ver los cuatro toasts de verdad
  (inicio, transcripcion lista, reporte listo, fallo) y confirmar el texto.

**Sin tocar, a proposito:** `LocalRecordingApiServer` sigue llamando a
`IMeetingPipeline` directo, asi que una grabacion disparada por HTTP tampoco
levanta estos eventos nuevos y sigue sin toast. Es el mismo hueco del backlog.

---
## T4.3 — Register() llevaba fallando desde que T4 existe (2026-08-23)

**Status: 🟡 Causa raiz encontrada y corregida; falta confirmar el arranque limpio.**
**Touches:** `Package.appxmanifest`, `App.xaml.cs` (LogDiagnostic + nota de la
ruta del log), `Services/ActivityNotificationService.cs` (traza).

Reportado por el usuario despues de T4.2: seguia sin ver toasts. T4.2 arreglo
un hueco real de cobertura de eventos, pero no era la causa de que no
apareciera nada, porque **T4 nunca mostro un solo toast.**

### La causa

```
[AppNotificationManager.Register] COMException 0x80004005
    No COM servers are registered for this app
```

En **cada arranque** desde que se implemento T4: 10:26, 10:36, 11:57 y 12:03
del 2026-08-23, y presumiblemente todos los anteriores. El `catch` de
`LaunchCore` hace exactamente lo que se diseno que hiciera — loguear y seguir
sin toasts — asi que la app arrancaba normal y el fallo no tenia superficie.

`Package.appxmanifest` no declaraba el activador COM. Una app **sin empaquetar**
se registra sola al llamar `Register()`; una **empaquetada** como esta necesita
declararlo en el manifiesto: `windows.comServer` con un `ExeServer` y
`windows.toastNotificationActivation` con el mismo `ToastActivatorCLSID`. El
manifiesto no tenia ni un bloque `<Extensions>`.

Esto invalida la nota de validacion de T4 que decia que el toast solo estaba
"pendiente de verificacion visual": no estaba pendiente, estaba roto.

### Por que tardo tanto en verse

`StartupErrorLogPath` usa `Environment.SpecialFolder.LocalApplicationData`. El
comentario del codigo explicaba que se evito `AppContext.BaseDirectory` porque
bajo WindowsApps la escritura se redirige de forma opaca — correcto, pero
incompleto: **corriendo empaquetada, `LocalApplicationData` tambien esta
redirigido.** El log no estaba en `%LOCALAPPDATA%\MeetingAssistant` sino en
`%LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\MeetingAssistant`.

Buscarlo en la ruta equivocada llevo a concluir dos veces que "Register() nunca
fallo porque no hay log". El comentario del codigo ahora dice donde buscarlo de
verdad.

> **Superado por T6a (2026-08-25).** Lo de arriba valia para el registro de
> desarrollo (`IsDevelopmentMode=True`, PFN `..._1z32rh13vfry6`). Sobre el MSIX
> firmado e instalado la redireccion **no** aplica: el log esta en
> `%LOCALAPPDATA%\MeetingAssistant\startup-errors.log`, la ruta plana. Medido en
> los arranques del 2026-08-25 09:52 y 10:11; el arbol `LocalCache` del PFN nuevo
> esta vacio. Ver las notas de validacion de T6a. Y ojo: al desinstalar el
> registro de desarrollo, `%LOCALAPPDATA%\Packages\..._1z32rh13vfry6\` **se borro
> con todo su log adentro** — la evidencia cruda de T4.3 y T4.4 (las
> `COMException`, el `Register OK` del 08-24, las trazas de los dos toasts) ya no
> existe en disco. Lo que queda es lo transcrito en este documento.

### El unico toast que si aparecio

El del 2026-08-23 11:58:59 que reporto el usuario no venia del pipeline: era el
globo de la bandeja (`_trayIcon.ShowError("Error de hotkey", ...)`) por
`InvalidOperationException: La transcripcion del audio ... vino vacia — no se
detecto habla` sobre una grabacion de 9 segundos. H.NotifyIcon lo entrega como
toast bajo el AUMID de la app, que es por que el contador de notificaciones se
movio y parecio que el canal funcionaba.

### Metodo de diagnostico, para reusarlo

El contador de toasts entregados por AUMID es observable y fiable en tiempo
real:

```
HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\{AUMID}
    PeriodicNotificationCount, LastNotificationAddedTime (FILETIME)
```

Sirve para distinguir "no se mostro" de "se mostro y no lo vi". Contrastado
mandando un toast de prueba al mismo AUMID con `ToastNotificationManager` desde
PowerShell: el contador subio al instante.

Se agrego `App.LogDiagnostic(string)` y trazas en
`ActivityNotificationService`: una al suscribirse y una por cada toast mostrado.
Sin eso, un toast que no aparece no deja rastro en ningun lado y no se puede
distinguir "el evento nunca llego" de "Show corrio y Windows no mostro nada".

### Verificacion

- `dotnet build MeetingAssistant.sln -t:Rebuild`: 0 errores, 0 advertencias.
- **Pendiente:** arrancar con el manifiesto nuevo y confirmar en el log
  `AppNotificationManager.Register OK` en vez de la `COMException`, y despues
  los cuatro toasts a ojo.

### Trampa de build, anotada porque ya mordio dos veces

Con la app corriendo, `dotnet build` reporta exito pero **no actualiza el layout
`AppX\`**: el proceso vivo tiene el `.exe` y el `.dll` tomados. El binario nuevo
queda en `win-x64\` y el que se ejecuta sigue siendo el viejo. Hay que cerrar la
app (bandeja > Salir) **antes** de compilar, y relanzar con `dotnet run`, que
ademas vuelve a registrar la identidad debug — necesario aca, porque el
activador COM se registra al registrar el paquete, no al compilar.

---

## T4.4 — El endpoint HTTP mataba el arranque entero (2026-08-24)

**Status: ✅ DONE.** Reportado por el usuario como una excepcion al arrancar.
**Touches:** `App.xaml.cs` (`LaunchCore`), mas un cambio de estado de maquina
fuera del repo.

```
[App.OnLaunched] System.Net.HttpListenerException (5): Access is denied.
   at MeetingAssistant.Infrastructure.Api.LocalRecordingApiServer.Start()
   at MeetingAssistant.App.App.LaunchCore()
```

Dos problemas distintos, y el segundo es el que convierte un fallo menor en una
app muerta.

### Causa 1 — una reserva de URL tapaba el prefijo (estado de maquina)

Existia una reserva en http.sys para `http://+:5757/` a nombre del usuario. El
codigo escucha en `http://localhost:5757/`. Los prefijos `localhost` **no**
necesitan reserva; es la *existencia* de la reserva de comodin fuerte sobre ese
puerto la que le niega el registro al prefijo especifico. Medido, sin elevar:

| Prefijo | Resultado |
|---|---|
| `http://localhost:5799/` (sin reserva) | OK |
| `http://localhost:5757/` | **Access denied (5)** |
| `http://+:5757/` (el reservado) | OK |

Descartados por medicion: el puerto estaba libre, no estaba en ningun rango
excluido (`netsh interface ipv4 show excludedportrange`), y no habia proceso
huerfano — un choque de puerto da 183, no 5.

Resuelto con `netsh http delete urlacl url=http://+:5757/` (elevado). Se eligio
borrarla en vez de agregar una para `localhost:5757`: esa reserva era un permiso
permanente para que cualquier proceso del usuario escuchara en 5757 en **todas
las interfaces** sin elevacion — la forma exacta de exposicion que T1.5 saco del
codigo.

**Origen desconocido.** El patron `+:{port}` coincide con el bind revertido de
`91f0ac0`, pero el log lo desmiente: el error aparece solo dos veces en todo el
historial, ambas el 2026-08-24. Si la reserva existiera desde el 08-11, los
arranques del 08-23 habrian fallado igual. Agregarla requiere elevacion.

### Causa 2 — `Start()` estaba fuera de todo `try/catch` (el defecto real)

`_apiServer.Start()` era la **primera sentencia** de `LaunchCore()` y no estaba
protegida. Al lanzar, la excepcion sube hasta el `catch` de `OnLaunched` y nada
de lo que sigue corre: ni `Register()` de notificaciones, ni el servicio de
toasts, ni `_window.Activate()`, ni la bandeja, ni el hotkey. Confirmado en el
proceso vivo: su unica ventana era `"MeetingAssistant - error al iniciar"`.

Contradice el criterio que el propio brief de T4 exige para las conveniencias
("una notificacion es conveniencia, no dependencia critica") y que
`TrayIconService` ya respetaba. El endpoint HTTP es igual de opcional: la bandeja
y el hotkey son los caminos principales, y una grabacion disparada por HTTP hoy
ni siquiera levanta los eventos del coordinador.

Envuelto en `try/catch` + `LogStartupFailure`, mismo patron que `TrayIconService`.

### Por que bloqueaba a T4.3

**Esta era la razon de que no hubiera ninguna traza de `Register` en el log**, ni
`OK` ni `COMException`: no se llegaba a esa linea. La verificacion pendiente de
T4.3 era imposible mientras esto estuviera roto, y el manifiesto ya estaba bien
desde `fb3f609`.

### Verificacion

- `dotnet build MeetingAssistant.sln -t:Rebuild`: 0 errores, 0 advertencias.
- Arranque limpio del usuario 2026-08-24 14:23, sin excepcion de `HttpListener`.
- `[diag] AppNotificationManager.Register OK` en el log a las 14:23:18.919 —
  **cierra el pendiente de T4.3.**
- `[diag] Toast mostrado por RecordingStarted` (14:24:12) y
  `Toast mostrado por RecordingFailed` (14:24:31) sobre una grabacion sin habla
  detectada. La traza prueba que `Show()` corrio sin lanzar; no prueba que
  Windows lo dibujara.
- **Confirmado en pantalla por el usuario (2026-08-25):** los dos toasts eran
  visibles con la ventana cerrada. Eso cierra el criterio de aceptacion critico
  de T4 — un fallo del pipeline sin ventana abierta ya tiene superficie visible,
  que es el hueco por el que T4 existe.
- **Sin verificar:** los toasts de `TranscriptReady` y `ReportSaved` — el camino
  de exito nunca se ejercito porque la transcripcion vino vacia. Tampoco se
  re-corrio el `POST` que devuelve 401 de T4.1.

### Hallazgo colateral: hay una segunda copia del proyecto

`D:\stuffProjectsCH\proyecto-codex-worker` es otro checkout de esta misma app
(commit `9183e0e` + "Task 2 Completed"), con el **mismo puerto 5757** y la
**misma identidad de paquete** (`962A0BC5-A1BC-432A-8A38-55011BFE3EE0`, identica
en los dos manifiestos). Consecuencias:

- Si las dos corren a la vez chocan en el puerto, en `RegisterHotKey` y en los
  dos iconos de bandeja. Peor: al compartir identidad comparten la clave de
  `AppInstance`, asi que lanzar una **redirige a la otra** en vez de arrancar.
- Comparten el directorio de log redirigido, o sea que `startup-errors.log`
  mezcla arranques de las dos copias. Tenerlo presente al leer este log como
  evidencia.
- Esa copia tiene el mismo defecto de `LaunchCore` sin corregir.

Para el pase de GUI de T4/T6b: asegurarse de que solo corra una copia.

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

### T5 validation — 2026-08-25

- El manifiesto declara exactamente una extensión `windows.startupTask` con
  `TaskId=MeetingAssistantStartup`, `Enabled=false`, ejecutable
  `MeetingAssistant.App.exe` y `EntryPoint=Windows.FullTrustApplication`. No se
  modificaron los bloques `windows.comServer` ni
  `windows.toastNotificationActivation`.
- `SettingsPage` ganó sólo el toggle **Iniciar con Windows** y su texto de
  estado. En cada `Loaded` vuelve a consultar `StartupTask.State`; no parte de
  un `false` local. `DisabledByUser` y `DisabledByPolicy` dejan el control
  apagado/deshabilitado y explican que debe corregirse desde Windows;
  `EnabledByPolicy` queda encendido/deshabilitado.
- Hubo iteración con registro de desarrollo. La instalación firmada existente
  no fue reemplazada automáticamente por `dotnet run`: la herramienta rechazó
  el registro hasta quitar el MSIX. Un primer `--no-build` además reutilizó un
  layout obsoleto con el Publisher/PFN anterior; se descartó y se ejecutó un
  `dotnet run --project src/MeetingAssistant.App` que construyó el manifiesto
  actual bajo el PFN real
  `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4`, con
  `IsDevelopmentMode=True`.
- Prueba GUI real: el toggle cargó apagado desde `StartupTask.State`; al
  encenderlo mostró el estado activo y `Task Manager > Startup Apps` mostró
  **Meeting Assistant / Enabled**. Al deshabilitarlo externamente en Task
  Manager y salir/volver a `SettingsPage`, el toggle se mostró apagado y
  deshabilitado con la explicación de `DisabledByUser`. No apareció un diálogo
  de consentimiento de Windows en esta identidad. No se reinició la máquina;
  se usó la alternativa de Task Manager admitida por los criterios.
- Sobre el MSIX firmado reinstalado se accionó el control mediante la API de
  accesibilidad de Windows y se leyó su estado después de cada operación:
  `Off` / "El inicio automático está desactivado." → `On` / "Meeting Assistant
  se iniciará cuando entres a Windows." → `Off` / "El inicio automático está
  desactivado.". Esto validó también el apagado desde la app, no sólo el cambio
  externo de Task Manager. No fue posible provocar `DisabledByPolicy` o
  `EnabledByPolicy` en esta máquina sin políticas administradas; esos dos
  caminos están implementados pero no fueron ejercitados en runtime.
- La app estuvo cerrada antes de compilar. `dotnet build MeetingAssistant.sln
  -t:Rebuild` terminó con 0 warnings y 0 errores. El empaquetado x64 firmado
  terminó con 0 errores y el warning ya conocido de `mspdbcmf.exe` ausente (no
  se generó paquete de símbolos); `Get-AuthenticodeSignature` devolvió `Valid`.
- Después de la iteración se quitó sólo el registro de desarrollo y se reinstaló
  `src/MeetingAssistant.App/AppPackages/MeetingAssistant.App_1.0.0.0_x64_Test/MeetingAssistant.App_1.0.0.0_x64.msix`.
  `Get-AppxPackage` mostró exactamente un paquete, `Status=Ok`, Publisher
  `CN=MeetingAssistant Local Publisher`, instalado bajo
  `C:\Program Files\WindowsApps` e `IsDevelopmentMode=False`. El manifiesto
  instalado volvió a confirmar la única extensión y `Enabled=false`.
- La instalación firmada se arrancó realmente y el proceso respondió. El AUMID
  confirmado sigue siendo
  `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App`. Esa ejecución
  agregó las entradas de las 15:46:45 a
  `%LOCALAPPDATA%\MeetingAssistant\startup-errors.log`; la ruta redirigida
  `%LOCALAPPDATA%\Packages\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4\LocalCache\Local\MeetingAssistant\startup-errors.log`
  no existía. Se comprobaron ambas antes de concluir dónde quedó la traza.
- `MeetingAssistant.Core.csproj` sigue siendo C# puro, sin referencias de
  plataforma/proveedor. El `appsettings.json` real continúa ignorado y no forma
  parte del cambio. T6b no se ejecutó.

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

**Partido en dos el 2026-08-22** (ver "Orden de cierre de Fase 3" arriba):
**T6a** son los pasos 1–3 de abajo — identidad real, certificado y `.msix`
instalable — y va **antes** de T5, porque sin identidad de paquete el
`StartupTask` de T5 no se puede validar. **T6b** es el paso 4 y los criterios
de aceptacion, que re-verifican T2–T5 sobre la instalacion real y cierran
Fase 3.

**Depends on:** T2 (T6a); T2–T5 completos (T6b).
**Touches:** `Package.appxmanifest`, packaging/signing config (new files —
certificate, publish profile — must be checked against `.gitignore`). Before
T6a, packaging output was ignored only below `src/MeetingAssistant.App/`; root
`AppPackages/` and certificate files anywhere in the repo were trackable. T6a
added root coverage for `*.pfx`, `*.cer` and `*.snk` before creating the
certificate and kept the package under the app project's ignored directory.

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

### T6a validation — 2026-08-25

- **Ignore protection ran before certificate creation.** `git check-ignore -v`
  resolved both `cert.pfx` at repo root and
  `src/MeetingAssistant.App/cert.pfx` to root `.gitignore:20`. It also verified
  the new `*.cer` and `*.snk` rules. No certificate appeared in `git status`.
- **Identity and certificate match exactly.** Manifest Publisher and
  certificate Subject are both `CN=MeetingAssistant Local Publisher`.
  `DisplayName`/visual display name are `Meeting Assistant`, and
  `PublisherDisplayName` is `MeetingAssistant Local Publisher`. The code-signing
  certificate thumbprint is `AD5A94D0DA131E47F395DD937721551C72AF5D52`, valid
  through 2029-08-25. Its private key lives in `Cert:\CurrentUser\My`; only the
  public `.cer` was exported, outside the repo, under
  `%LOCALAPPDATA%\MeetingAssistant\Signing\`.
- **One-time trust step was required.** `CurrentUser\TrustedPeople` (and even
  `CurrentUser\Root`) was insufficient for `Add-AppxPackage`, which failed with
  `0x800B0109`. An elevated `certutil -addstore TrustedPeople <cer>` imported
  the public certificate into `LocalMachine\TrustedPeople`; after that,
  `Get-AuthenticodeSignature` returned `Valid` and installation succeeded.
- **Package built, signed and installed for x64.** Output:
  `src/MeetingAssistant.App/AppPackages/MeetingAssistant.App_1.0.0.0_x64_Test/MeetingAssistant.App_1.0.0.0_x64.msix`.
  The old development registration (`CN=AppPublisher`, `IsDevelopmentMode=True`)
  was removed before installation. `Get-AppxPackage` then showed exactly one
  package, `Status=Ok`, `IsDevelopmentMode=False`, installed under
  `C:\Program Files\WindowsApps`, with the new Publisher. `SignatureKind` is
  `Developer`, as expected for the trusted self-signed sideload certificate.
- **New identifiers:** PFN
  `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4`; AUMID
  `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App`. Toast diagnostics
  must use
  `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App`.
  That registry key did not exist yet because T6a did not fire a toast; firing
  and clicking real toasts remains in T6b.
- **Observed diagnostic-log location differs from the pre-T6a prediction.** On
  the installed full-trust MSIX, the effective path is
  `%LOCALAPPDATA%\MeetingAssistant\startup-errors.log`, not either PFN's
  `LocalCache` tree. The 2026-08-25 09:52 and 10:11 installed launches both
  appended `AppNotificationManager.Register OK` there. Debugging this installed
  package must use that observed path; the former
  `%LOCALAPPDATA%\Packages\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_1z32rh13vfry6\...`
  path is stale, and the analogous new-PFN path was absent.
- **Actual installed launch:** invoked through the registered AppsFolder/Start
  activation for the AUMID above, never `dotnet run`. The running executable
  came from `C:\Program Files\WindowsApps\...\MeetingAssistant.App.exe`; the
  HTTP listener returned `401` to a request without its token; and a desktop
  capture visually confirmed the `Meeting Assistant` window plus the tray icon
  matching `Assets/TrayIcon.ico` in the notification-area overflow.
- The real `appsettings.json` was confirmed present inside the installed
  package. Therefore this MSIX contains API keys: it must stay local and must
  not be shared. A distributable build requires a separate future migration of
  configuration to `%LOCALAPPDATA%`; that was documented, not implemented in
  T6a.
- The app process was stopped before both packaging and the final rebuild; the
  terminal could not invoke the tray menu during those pre-build closures, so
  it stopped the process and verified it was absent before compiling. Final
  `dotnet build MeetingAssistant.sln -t:Rebuild` completed with 0 warnings and
  0 errors. The MSIX packaging run completed with 0 errors and one tooling-only
  warning because `mspdbcmf.exe` was not installed, so no symbols package was
  generated; the `.msix` itself was generated and signature-validated.
- T5 and T6b were not exercised. In particular, this pass did not validate the
  hotkey, success-path toasts, autostart, or uninstall cleanup against the
  installed package.

### T6b validation — 2026-08-25 (parcial, en curso)

**Contexto de máquina — leerlo primero, porque cambia cómo se interpreta todo
lo anterior.** Este pase corrió en
`C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant`, en otra máquina y
otro perfil de Windows.
Los briefs de T6a/T5 apuntan a `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
y **esta máquina no tiene disco D: en absoluto**. Medido antes de empezar: sin
`AppPackages/`, sin `.pfx`/`.cer` en ningún lado, `%LOCALAPPDATA%\MeetingAssistant\`
inexistente, y ningún certificado `MeetingAssistant` ni en `LocalMachine\TrustedPeople`
ni en ningún store de `CurrentUser`. Lo único registrado era un **registro de
desarrollo obsoleto** (`IsDevelopmentMode=True`, `SignatureKind=None`, PFN
`..._1z32rh13vfry6`) cuyo `AppX\AppxManifest.xml` todavía llevaba el placeholder
pre-T6a `CN=AppPublisher` y databa del 2026-08-23. Conclusión: **la instalación
firmada que validaron T6a y T5 no existe en esta máquina**, así que hubo que
reproducir T6a acá antes de poder empezar T6b. Las validaciones de T6a/T5 siguen
siendo válidas para la otra máquina; no lo son para esta.

- **Material de firma nuevo, distinto del de T6a.** Thumbprint
  `9B006BCB1FD6DD96187A8B6678EA4C9F9C7221B7`, Subject
  `CN=MeetingAssistant Local Publisher` — idéntico al del manifiesto, que es lo
  que mantiene el PFN sin cambios. Válido hasta 2029-08-25. Clave privada en
  `Cert:\CurrentUser\My`; sólo el `.cer` público exportado fuera del repo, a
  `%LOCALAPPDATA%\MeetingAssistant\Signing\`. **El thumbprint `AD5A94D0…` que
  registra T6a no está en esta máquina** — toda instrucción de limpieza que lo
  nombre aplica sólo a la otra.
- **El anclaje de confianza volvió a requerir elevación**, igual que en T6a:
  `certutil -addstore TrustedPeople` elevado por UAC, exit code 0, y después
  `Get-AuthenticodeSignature` dio `Valid`. Se confirma que no es una rareza de
  aquella máquina: es un paso obligatorio del procedimiento.
- `git check-ignore -v` corrido **antes** de generar el certificado: `.gitignore:20`
  cubre `*.pfx` y `.gitignore:21` cubre `*.cer`, tanto en la raíz como bajo el
  directorio de la App. `git status` quedó limpio; `AppPackages/` aparece sólo
  como ignorado.
- **Configuración de empaquetado: Debug, a propósito.** En Release el csproj
  activa `PublishTrimmed=True` y `PublishReadyToRun=True`, y recortar una app
  WinUI con MVVM/DI por reflexión más los SDKs de proveedor puede romper en
  runtime — eso contaminaría un pase de aceptación **de comportamiento** con
  fallos que no son defectos del producto. Las notas de T6a no dejaron registrada
  qué configuración usaron, así que no hay con qué comparar. Salida:
  `src/MeetingAssistant.App/AppPackages/MeetingAssistant.App_1.0.0.0_x64_Debug_Test/MeetingAssistant.App_1.0.0.0_x64_Debug.msix`.
  0 errores y la misma advertencia sólo-de-tooling por `mspdbcmf.exe` ausente que
  vio T6a. `makeappx`/`signtool` salieron del caché de NuGet
  (`microsoft.windows.sdk.buildtools`): esta máquina **no** tiene el Windows 10
  SDK instalado en `Program Files`.
- **Instalado de verdad:** firma `Valid`, después `Add-AppxPackage`. Resultado —
  exactamente un paquete, `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_1.0.0.0_x64__n5p1q6rt9wnn4`,
  `SignatureKind=Developer`, `Status=Ok`, `IsDevelopmentMode=False`, bajo
  `C:\Program Files\WindowsApps`. Identidad idéntica al PFN/AUMID que registró T6a.

#### La lista de T6b — resultado real

**A. Arranque e identidad — MEDIDO, los tres items.**

- Arrancó por `shell:AppsFolder\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App`,
  nunca `dotnet run`. Proceso vivo (PID 10912) con `Path` =
  `C:\Program Files\WindowsApps\...\MeetingAssistant.App.exe`.
- `%LOCALAPPDATA%\MeetingAssistant\startup-errors.log` (la ruta plana que observó
  T6a) con `AppNotificationManager.Register OK` a las 22:46:58 y **sin** excepción
  de `LocalRecordingApiServer.Start`.
- Un solo paquete registrado. Puerto 5757 en `Listen` (owner PID 4 = HTTP.sys, lo
  esperable para `HttpListener`).

**Paso 0 — el fix de `2add4df`: MEDIDO, funciona.**

Antes de la corrida `%LOCALAPPDATA%\MeetingAssistant\meeting-output` **no existía**.
Una grabación disparada por el endpoint HTTP lo creó y escribió
`meeting-20260825-224749.wav` (287.578 bytes, 8 s). **Es la primera grabación que
corre bajo el paquete instalado en la historia del proyecto**, y no se topó con el
`Access to the path ... is denied` que bloqueaba T6b. El fix queda confirmado
contra la instalación de sólo lectura, no sólo leído en el código.

**F. Endpoint HTTP — MEDIDO.**

- `POST /recording/start` sin `X-Api-Token` → **401**. Con token inválido → **401**.
- Con token válido → **200** `{"status":"recording"}`.
- `POST /recording/stop` → **409**, y **no es un defecto**: el handler mapea toda
  `InvalidOperationException` a 409, y lo que llegó fue el guard de transcripción
  vacía — los 8 s grabados eran silencio. Un segundo `stop` devolvió 409 con
  `"No hay una captura de audio en curso para detener."`, lo que prueba que el
  primero **sí** detuvo la captura y el pipeline volvió a idle.
- Como anticipaba el brief, la grabación por HTTP no produjo ningún toast (hueco
  conocido de `LocalRecordingApiServer` → `IMeetingPipeline`, backlog).
- **Observación nueva, no arreglada acá:** los fallos que llegan por el camino
  HTTP **no** quedan en `startup-errors.log` — sólo viajan en el cuerpo de la
  respuesta. Si una grabación disparada por HTTP falla y nadie mira la respuesta,
  no queda rastro en ningún lado. Va al backlog junto con el hueco de eventos.

**B, C, D, E, G — NO VERIFICADO.** Ninguno se puede confirmar desde una terminal:
requieren ver la pantalla. Quedan intactos para la primera sesión con GUI —
icono de bandeja y su icono correcto, cerrar-oculta, "Salir" sin proceso huérfano,
el label "Detener grabación" de T2.2, el hotkey `Ctrl+Alt+F9` con la ventana
cerrada, los cuatro toasts (incluidos `TranscriptReady` y `ReportSaved`, que
necesitan **una grabación con habla real** que llegue a guardar reporte), que
ningún toast filtre contenido de la reunión, el clic en un toast devolviendo la
ventana al frente sin abrir un segundo proceso, y el toggle de autostart contra
`Administrador de tareas > Aplicaciones de inicio`.

**Baseline para diagnosticar toasts:** la clave
`HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App`
**todavía no existe** en esta máquina — ningún toast se entregó aún bajo esta
identidad acá. La crea el primero, así que su aparición ya es señal.

**H. Desinstalación limpia — NO EJECUTADO, deliberadamente.** Destruye la
instalación de trabajo y el brief la manda al final. Cuando se corra, en **esta**
máquina el certificado a sacar a mano de `LocalMachine\TrustedPeople` es
`9B006BCB1FD6DD96187A8B6678EA4C9F9C7221B7`, no el de T6a. Sigue pendiente la
decisión explícita sobre si `%LOCALAPPDATA%\MeetingAssistant\` se borra o se
conserva: hoy ya contiene el log, `Signing\` con el `.cer`, y **audio de una
reunión** en `meeting-output\`.

**Estado del entorno al terminar esta sesión:** paquete firmado instalado, app
corriendo desde `WindowsApps`, un solo paquete registrado, repo limpio. Todo listo
para que el pase visual arranque sin preparación previa. **Cuidado: un `dotnet run`
reemplaza esta instalación firmada y anula el pase** — si pasa, hay que
reconstruir el `.msix` y reinstalarlo antes de seguir.

#### Segunda tanda — 2026-08-25 noche, ya con GUI del usuario

**B, C, D, E — VISTOS por el usuario contra el paquete instalado.** El usuario
confirmó explícitamente que funcionan: bandeja e icono, cerrar-oculta y "Salir",
hotkey `Ctrl+Alt+F9` con la ventana cerrada, los toasts, y el clic en un toast
devolviendo la ventana al frente. Con esto **el camino de éxito de T4 queda visto
en pantalla**, que era el hueco abierto desde el 08-24. Único item de estas
secciones que **no** se reportó: **T2.2** (ver abajo).

**El pipeline completo corrió end-to-end bajo el paquete instalado — MEDIDO, y es
la primera vez.** Una grabación disparada por `POST /recording/start` capturó
audio real (no silencio) y el `POST /recording/stop` respondió **200** con
transcript real de Deepgram, reporte extraído por el LLM y `savedReportPath`. El
`.md` correspondiente apareció en el vault de Obsidian a las 23:35:24, el mismo
instante del stop. O sea: captura → `.wav` en `%LOCALAPPDATA%` → transcripción →
extracción → guardado en vault, todo desde `C:\Program Files\WindowsApps`. Esto
cierra la duda de fondo que arrastraba T6b: **el producto funciona instalado**, no
sólo bajo `dotnet run`. (Los `.wav` quedaron en `meeting-output\`; el reporte, en
el vault configurado — la ruta no se escribe acá a propósito, ver el incidente de
seguridad de `AGENTS.md`.)

**T2.2 — NO VERIFICADO, sigue pendiente desde el 08-21.** Se dejó una grabación
corriendo por HTTP a propósito para que el usuario hiciera el clic derecho en la
bandeja y leyera el label; no llegó reporte de qué mostró. Es el único item de la
sección B que sigue sin resolverse, y es exactamente el que decide si
`RightClickCommand` corre antes de que se construya el menú nativo. **Cuesta 10
segundos y necesita una grabación viva.**

**G — autostart: el toggle no se reflejaba. Diagnóstico parcial, sin fix.**
Síntoma reportado: mover el switch y no ver cambio en `Administrador de tareas >
Aplicaciones de inicio`. Lo medido:

- El estado en el registro era `State=0` (`Disabled`) con `UserEnabledStartupOnce=1`
  y un `LastDisabledTime` reciente. Ojo con la lectura: `Disabled` **no** es
  `DisabledByUser` — cuando el usuario apaga la tarea desde el Administrador de
  tareas, Windows deja `DisabledByUser`. Así que hubo un `Disable()` pedido por la
  app, y un enable previo que sí funcionó.
- **La API y el manifiesto están sanos.** Se llamó a la WinRT directamente con la
  identidad del paquete (`Invoke-CommandInDesktopPackage` + `StartupTask.GetAsync`):
  estado antes `Disabled`, `RequestEnableAsync()` devolvió **`Enabled`**, y una
  segunda lectura confirmó `Enabled`. El registro pasó a `State=2`. La declaración
  `uap5:Extension Category="windows.startupTask"` del manifiesto es correcta y
  `GetAsync(TaskId)` resuelve.
- **Nada más en el código toca la StartupTask**: `StartupTaskService` sólo se usa
  desde `SettingsPage`. Queda descartado que algo la apague al arrancar.
- **Hipótesis principal, no confirmada:** la pestaña "Aplicaciones de inicio" del
  Administrador de tareas **no se refresca sola**. Si estaba abierta desde antes,
  el cambio no aparece por más que el toggle funcione, y la secuencia
  enable → "no veo nada" → disable explica el estado del registro **sin que haya
  defecto alguno**. La alternativa —que el camino de lectura/escritura de
  `SettingsPage` esté roto— sigue viva pero sin evidencia.
- **Medición que lo decide, pendiente:** con el autostart **hoy en `Enabled`**,
  abrir el Administrador de tareas *cerrándolo y reabriéndolo* y ver si lista
  "Meeting Assistant"; después abrir Configuración en la app y ver si el toggle se
  muestra en ON. Si la página lo muestra en OFF mientras el SO dice `Enabled`, el
  defecto está en `GetStateAsync`/`ApplyState` y es concreto. **No se tocó
  `StartupTaskService` porque no hay defecto demostrado** — un fix a ciegas acá
  sería inventar la causa.

**Observación menor, al backlog:** los nombres de archivo no usan la misma base de
tiempo. El `.wav` sale con hora local (`meeting-20260825-232829.wav`) y el reporte
con UTC (`assignment-meeting-20260826-063524.md`) para la misma grabación. No
rompe nada, pero al ordenar por nombre una reunión de la noche aparece con fecha
del día siguiente.

**Estado de la máquina al cerrar esta sesión:** paquete firmado instalado y app
corriendo; **el autostart quedó en `Enabled`**, habilitado por la sonda de la API,
no por la UI — si no se quiere, se apaga desde el toggle o desde el Administrador
de tareas; cuatro `.wav` en `%LOCALAPPDATA%\MeetingAssistant\meeting-output\`, dos
de ellos con audio real; y un reporte nuevo en el vault. La desinstalación (H)
sigue sin ejecutarse.

### T6b cierre — 2026-08-26 (máquina `D:`, la de T6a/T5)

**Contexto de máquina.** Esta sesión corrió en
`D:\stuffProjectsCH\consoleApp_MeetingsAssistant`, que es la máquina de T6a/T5
— **no** la de las notas del 08-25, que son de
`C:\Projects\PersonnalTool_App\...`. Acá el certificado es
`AD5A94D0DA131E47F395DD937721551C72AF5D52`, no el `9B006BCB…` de la otra.

**Estado encontrado al empezar: la instalación firmada ya no existía.**
`Get-AppxPackage` daba `SignatureKind=None`, `IsDevelopmentMode=True`, y el
proceso corría desde
`src\MeetingAssistant.App\bin\x64\Debug\...\AppX\MeetingAssistant.App.exe`. O
sea, un `dotnet run` había reemplazado el paquete firmado, exactamente el
escenario contra el que advierte la precondición del brief. Se rehizo el
paquete antes de validar nada.

**Configuración del paquete: Debug, a propósito**, con el mismo criterio que
usó la otra máquina el 08-25 — en Release el csproj activa `PublishTrimmed` y
`PublishReadyToRun`, y recortar WinUI + MVVM/DI por reflexión puede romper en
runtime, contaminando un pase de aceptación *de comportamiento*. Nota nueva:
**el `.msix` que produjo T6a en esta máquina era Release** (carpeta
`..._x64_Test`, sin `Debug` en el nombre) y **nunca ejercitó el pipeline**. No
hay ninguna evidencia de que el paquete Release recortado pueda transcribir y
extraer; el único que corrió end-to-end, acá y en la otra máquina, es Debug.
Salida: `AppPackages\MeetingAssistant.App_1.0.0.0_x64_Debug_Test\MeetingAssistant.App_1.0.0.0_x64_Debug.msix`,
0 errores y la misma advertencia sólo-de-tooling por `mspdbcmf.exe` ausente.
Firma `Valid`, instalado con `SignatureKind=Developer`,
`IsDevelopmentMode=False`, un solo paquete.

**A. Arranque e identidad — MEDIDO, los tres items.** Arrancó por
`shell:AppsFolder\...!App`; proceso desde `C:\Program Files\WindowsApps\...`;
`AppNotificationManager.Register OK` en la ruta plana **sin** excepción de
`LocalRecordingApiServer.Start`; un solo paquete; puerto 5757 en `Listen`
(owner PID 4 = HTTP.sys).

**B. T2.2 — PASA. Visto por el usuario. Cerrado desde el 08-21.** Con una
grabación viva disparada por `POST /recording/start`, el clic derecho en la
bandeja mostró **"Detener grabación"**. Queda contestada la pregunta que
originó T2.2: `RightClickCommand` **sí** corre antes de que se construya el
menú nativo. El icono de bandeja además se vio correcto (el custom, no el
genérico), o sea que la excepción de carga de icono del 08-25 11:00 no
reaparece bajo este paquete.

**F. Endpoint HTTP — MEDIDO.** Sin token → 401; token inválido → 401; token
válido → 200 `{"status":"recording"}`. El `stop` devolvió 409 por el guard de
transcripción vacía (era silencio), y un segundo `stop` devolvió 409 con
`"No hay una captura de audio en curso para detener."`, probando que el primero
sí detuvo la captura y el pipeline volvió a idle. La grabación escribió
`meeting-20260826-102657.wav` bajo `%LOCALAPPDATA%`, confirmando otra vez el fix
del paso 0. Sin toast, como estaba anticipado (hueco conocido del backlog).

> **Trampa de medición, anotada para no repetirla:** leer el cuerpo de un 409
> con `Invoke-WebRequest` + `$_.Exception.Response.GetResponseStream()` devuelve
> **vacío** — el stream ya fue consumido. Parecía que el 409 no traía cuerpo, y
> sí lo trae (`WriteJsonAsync` escribe `error` + `details`). Con
> `System.Net.Http.HttpClient` se lee bien. No era un defecto del producto.

**G. T5 autostart — NO ES DEFECTO. Medición decisiva hecha.** Se confirmó la
hipótesis principal que dejó abierta el 08-25: el Administrador de tareas no se
refresca solo. Secuencia, con baseline limpio (`State=0`,
`UserEnabledStartupOnce=0`, que dejó la reinstalación) y **con el Administrador
de tareas cerrado de entrada**:

1. El usuario encendió el toggle en `SettingsPage` → texto
   *"Meeting Assistant se iniciará cuando entres a Windows."*
2. Verificación independiente por registro: `State=2` (`Enabled`),
   `UserEnabledStartupOnce=1`. **El camino de escritura de la UI llega al SO** —
   esto es lo que el 08-25 no se había podido separar.
3. Administrador de tareas **abierto de cero** → "Meeting Assistant" listado y
   **Habilitado**.
4. Navegar fuera de Configuración y volver (re-dispara `SettingsPage_Loaded` →
   `GetStateAsync`) → el toggle sigue en **ON**. **El camino de lectura también
   es correcto.**

Se mantiene la decisión del 08-25 de **no tocar `StartupTaskService`**: no había
defecto que arreglar, y el fix a ciegas habría inventado una causa. `DisabledByPolicy` /
`EnabledByPolicy` siguen **no verificables en esta máquina** (sin directiva
administrada), no verificados.

**H. Desinstalación limpia — EJECUTADA Y PASA, todos los items.** Se
desinstaló con `Remove-AppxPackage` (la misma API que llama Configuración >
Aplicaciones), **a propósito con la app corriendo**, porque "no queda proceso de
bandeja huérfano" es justamente el criterio:

| Item | Resultado |
|---|---|
| Paquete removido | ✅ 0 paquetes; el directorio de `WindowsApps` desapareció |
| Proceso de bandeja huérfano | ✅ ninguno — el proceso murió con la desinstalación |
| Puerto 5757 | ✅ liberado |
| StartupTask registrada | ✅ la clave `SystemAppData\...\MeetingAssistantStartup` se borró **entera**, aunque estaba en `State=2` (`Enabled`) al momento de desinstalar |
| Reserva `urlacl` de 5757 | ✅ ninguna quedó — **el modo de falla de T4.4 no se repite** |
| Certificado de confianza | ⚠️ **sobrevive**, como predecía el brief |
| `%LOCALAPPDATA%\MeetingAssistant\` | ⚠️ **sobrevive** — log, `Signing\`, y 4 `.wav` (~130 MB) |

**La limpieza manual del certificado se ejecutó y se verificó, no se asumió.**
`certutil -delstore TrustedPeople AD5A94D0…` elevado: la cuenta de certificados
`MeetingAssistant` en `LocalMachine\TrustedPeople` pasó de **1 → 0**. Después
`certutil -addstore` con el `.cer` de `%LOCALAPPDATA%\MeetingAssistant\Signing\`
lo restauró: **0 → 1**. O sea, el paso manual que hace verdadera la
"desinstalación limpia" está probado en ambas direcciones, y el `.cer` exportado
sirve para rehacer el anclaje sin regenerar nada.

**Decisión explícita sobre `%LOCALAPPDATA%\MeetingAssistant\` — la pedía el
brief y acá queda tomada: se CONSERVA. La desinstalación no lo toca, y eso es
intencional**, no un descuido: el log de diagnóstico, el `.cer` exportado y el
audio de las reuniones son datos del usuario, y se borran a mano si se quiere.
Consecuencia asumida: `meeting-output\` **crece sin límite** — hoy 4 archivos,
~130 MB, dos de ellos reuniones reales de ~60 MB. **No hay política de retención
y eso va al backlog**, no a Fase 3.

**Reinstalación posterior — hecha, la máquina queda usable.** Paquete firmado
reinstalado, `IsDevelopmentMode=False`, un solo paquete, arrancado por AUMID con
`Register OK` a las 11:19:12. **Ojo: la desinstalación reseteó el autostart a
`State=0` (Disabled)** — el toggle que se encendió en el paso G quedó apagado.
Si se quiere autostart, hay que volver a encenderlo.

#### Lo que NO se verificó en esta máquina — dicho explícitamente

**C (hotkey) y D (los cuatro toasts) no se re-ejercitaron contra este paquete
Debug.** Siguen validados por confirmación del usuario del 2026-08-25 contra la
instalación de la otra máquina, y por eso T4/T4.1/T4.2 siguen cerrados; pero
esta combinación exacta de paquete y código (Debug + `f4ae7d4`) no los ejerció.
Se intentó y **se descartó por decisión del usuario**, no por falta de tiempo.

Se registra además, porque es el tipo de cosa que este documento existe para no
perder: **un primer reporte de que la corrida end-to-end había pasado no se
sostuvo contra la máquina.** Medido en el momento: log sin líneas nuevas (4669
bytes, sin ningún `Toast mostrado por…` posterior al arranque de las 10:25:51),
contador de toasts del AUMID sin moverse (9), ningún `.wav` nuevo salvo el del
test HTTP, y el vault con los mismos 5 reportes — el más nuevo de las 10:08:52,
**anterior a la reinstalación**, y con `type: functional-spec`, no
`feature-handoff`. La corrida de las 10:01–10:08 fue bajo el **registro de
desarrollo viejo**, antes del paquete nuevo. Se anotó como no verificado en vez
de darlo por bueno.

**Hallazgo colateral: `appsettings.json` real no tiene sección `Hotkey`.** El
hotkey funciona igual porque `GlobalHotkeyService.ReadHotkey` cae a los defaults
del código (`"Control+Alt"` / `"F9"`), así que `Ctrl+Alt+F9` es correcto. No es
un defecto, pero **cambiar el hotkey requiere agregar la sección**, no editar una
existente.

### Acceptance criteria
- A freshly-installed (not `dotnet run`) copy of the app launches from the
  Start Menu, shows the tray icon, responds to the hotkey, serves the local
  HTTP endpoint, fires toast notifications, and honors the autostart toggle
  — i.e., T2–T5 all re-verified against the real packaged install, not just
  `dotnet run`.
- Uninstalling via Windows Settings > Apps cleanly removes the app (no
  orphaned startup task registration, no orphaned tray icon process).
- **Anclaje de confianza, agregado despues de T6a:** instalar el paquete requirio
  un `certutil -addstore TrustedPeople` **elevado** en `LocalMachine`, porque
  `CurrentUser\TrustedPeople` no alcanzo (`Add-AppxPackage` fallaba con
  `0x800B0109`). Eso es estado de maquina que una desinstalacion por
  Configuracion **no** revierte: el certificado queda como anclaje de confianza
  valido hasta 2029-08-25, thumbprint `AD5A94D0DA131E47F395DD937721551C72AF5D52`.
  La limpieza tiene que sacarlo tambien
  (`Get-ChildItem Cert:\LocalMachine\TrustedPeople`), o el criterio de
  "desinstalacion limpia" es falso. Precedente directo: la reserva de urlacl de
  T4.4, que sobrevivio nueve dias a la reversion de su codigo y despues rompio
  otra cosa.
- The signing certificate file is not present in `git status` / not tracked.

---

## T7 — Startup diagnostics, configuration validation, and the 2026-08-13 launch crash

**Status: ✅ DONE** — built, run and verified 2026-08-13. Unplanned work: this
started as a live crash blocking the user, not as task-graph planning.

### How it presented

The user reported that running the app showed "a message about an invalid
debugger installed" and then **opened a second Visual Studio instance**. That
framing is misleading and cost time on the previous attempt too — worth
recording so nobody re-diagnoses it as a broken IDE:

```
app throws unhandled  →  escapes to XAML framework  →  0xc000027b
                      →  Windows Error Reporting
                      →  AeDebug = vsjitdebugger.exe, Auto unset
                      →  JIT debugger dialog  →  "new instance of Visual Studio"
```

The second VS window is Windows' Just-In-Time debugger doing exactly what it is
configured to do. **The Visual Studio installation was never the problem.** Do
not "repair" VS, and do not disable JIT debugging — that only hides the next
crash.

### Root cause (confirmed, not inferred)

`appsettings.json` had drifted from `appsettings.example.json`: the example
documents an `Api` section (`Port`, `AuthToken`), the real local file had no
`Api` section at all. `LocalRecordingApiServer`'s **constructor** throws when
`Api:AuthToken` is missing — correctly, since that endpoint opens the
microphone. DI resolved it inside `App.OnLaunched`, nothing caught it, and it
became the `0xc000027b` above.

Note this is a *different* cause from the 2026-08-12 crash recorded under T2,
which was diagnosed as the tray icon. Both produced an identical native fault
signature, which is exactly why that signature is not sufficient evidence on
its own.

### Diagnostic evidence gathered (for reuse next time)

| Check | Command | Result at the time |
|---|---|---|
| Fault record | `Get-WinEvent` Application log, `Application Error` | `0xc000027b` in `Microsoft.UI.Xaml.dll` |
| Package registration | `Get-AppxPackage` | registered dev-mode, `Status: Ok` |
| Developer Mode | `HKLM:\...\AppModelUnlock` | enabled |
| Debug registration | `HKCU:\...\ActivatableClasses\Package\<PFN>\DebugInformation` | absent — ruled out a stale debugger hook |
| JIT config | `HKLM:\...\AeDebug` | `vsjitdebugger.exe`, `Auto` unset → prompts |

### What was implemented

1. **`Views/StartupErrorWindow.cs` (new)** — last-resort error window, built
   entirely in code with no XAML and no `InitializeComponent`. Deliberate: it
   must be able to report failures that happened *while* loading XAML or
   building the DI container, so it cannot depend on either.
2. **`App.xaml.cs`** — `RegisterGlobalExceptionHandlers()` runs first in the
   constructor (XAML `UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`); `ConfigureServices()` can no longer
   throw out of the constructor (failure is stashed and reported from
   `OnLaunched`); `OnLaunched`'s body moved into `LaunchCore()` inside a
   try/catch. The error window is only shown while `_window is null` — once the
   main window is alive, a stray async failure is logged but does not interrupt
   the user.
3. **`Services/StartupConfigurationValidator.cs` (new)** — runs before anything
   is registered and reports **every** missing key at once instead of failing on
   the first. Provider-aware (`Llm:Provider` decides whether Gemini or
   AzureFoundry credentials are required); `AzureFoundry:ApiKey` is deliberately
   optional because Azure.Identity is a valid alternative. Uses the same
   placeholder rule as `App.ReadSetting` (a value starting with `<` counts as
   missing) so validating and reading cannot disagree.

### Log location — corrected, this is easy to get wrong

`startup-errors.log` was moved off `AppContext.BaseDirectory` (unwritable under
`WindowsApps` when packaged — which is why no log ever appeared despite repeated
crashes) to `LocalApplicationData`. **But for a packaged run Windows redirects
that into the package container:**

```
%LOCALAPPDATA%\Packages\962A0BC5-...__1z32rh13vfry6\LocalCache\Local\MeetingAssistant\startup-errors.log
```

Not `%LOCALAPPDATA%\MeetingAssistant\`. Looking in the unredirected path makes a
working log look like a broken one.

### Verification actually performed (not read-only)

- `dotnet build MeetingAssistant.sln` — 0 errors, 0 warnings.
- App launches, stays up, port 5757 listening, `POST /recording/stop` without a
  token returns **401**.
- Validator exercised for real by removing `Api:AuthToken` **and**
  `AzureFoundry:Deployment`, then running the packaged app — both were reported
  together in one message. Config restored and re-verified afterward.

### Correction to the T2 tray-icon diagnosis above

The 2026-08-12 fix (swap `Assets/AppIcon.ico` → `Assets/Square44x44Logo.targetsize-24_altform-unplated.png`,
`TrayIconService.cs:58`) **did not work, and made the failure harder to see.**
First real managed stack trace, captured 2026-08-13:

```
System.ArgumentException: Argument 'picture' must be a picture that can be used as a Icon.
   at System.Drawing.Icon.Initialize(...)
   at H.NotifyIcon.StreamExtensions.ToSmallIcon(Stream stream)
   at H.NotifyIcon.ImageExtensions.ToIconAsync(ImageSource, CancellationToken)
   at H.NotifyIcon.TaskbarIcon.<OnIconSourceChanged>d__163.MoveNext()
```

`ToSmallIcon` feeds the stream to `System.Drawing.Icon(Stream)`, which requires
an **ICO** stream. A PNG is *less* usable there than the original `.ico`, not
more. It now fails **asynchronously** (a `Task` continuation posted to the
dispatcher), so the `try/catch` around `AttachTo()` never sees it — which is why
it stayed invisible until the global handlers from T7 caught it.

**Consequence — a real usability trap, not cosmetic:** `MainWindow` handles
close with `args.Cancel = true; AppWindow.Hide()`. With no working tray icon,
closing the window leaves the process running and unreachable, still holding
port 5757 — so the *next* launch fails to bind with
`HttpListenerException (183): ... conflicts with an existing registration`.
This was hit twice during testing.

**Correct fix (not yet applied):** supply a small single-frame **BMP-encoded
`.ico`** (16×16/32×32) as a dedicated tray asset, added as `Content` in the
`.csproj`, and point `IconSource` at it. Do not use a PNG, and do not use the
multi-frame `AppIcon.ico`. Re-confirm by launching and checking the log is
clean.

### Related config changes made in the same session

- `Storage:VaultPath` in both `appsettings.json` files pointed at a directory
  that did not exist (4 missing segments). `MarkdownReportStorage.cs:34` calls
  `Directory.CreateDirectory`, so this would **not** have thrown — it would have
  silently created an empty tree and written reports somewhere that is not the
  Obsidian vault. At the user's request it now points at `local-vault/` in the
  repo root (added to `.gitignore`, since it holds meeting content). The
  original value is preserved in each file as `Storage:VaultPathOriginal` so it
  can be restored.
- `MeetingAssistant.Harness/appsettings.json` was missing entirely, breaking
  `dotnet build MeetingAssistant.sln` with `MSB3030`. Present now; confirmed
  covered by `.gitignore` line 15.

### Follow-ups left open

- Tray icon `.ico` fix described above (folds back into T2 / T2.1).
- Config validation covers **presence only**, by decision. It would not catch a
  `Storage:VaultPath` that is well-formed but points nowhere real — the exact
  problem found above. Extending it to resolve paths is a candidate if that
  recurs.
- `MeetingAssistant.Harness` still duplicates `ReadRequiredSetting`/`ReadSetting`
  from `App.xaml.cs` and does not call the validator, so it retains the original
  fail-on-first-missing-key behaviour.

---

## T8 — Prompt catalog after transcript + report viewer

**Status: ✅ DONE** — 2026-08-14, including same-day follow-ups T8.1–T8.3.
**Depends on:** Fase 1 extractor (done). Independent of T2.1 / T3.
**Touches:** `MeetingAssistant.Core` (catalog, prompt, extractor, pipeline
split — still no provider packages), `Infrastructure/Storage`,
`App` RecordPage/ViewModel/coordinator + Markdig preview,
`Harness` extract-from-transcript path.

### Why
The extractor had a single hardcoded system prompt
(`ReportExtractionPrompt` v1 — assignment-meeting JSON). As soon as a
transcript existed, the only thing the app could do was run that prompt
and save a `MeetingReport`. Real use needs more than one report shape
and a pause after the transcript: pick a prompt, **view it**, generate,
**view the report** (rendered, not only raw), and find the file in the
Obsidian vault.

### What landed

**T8 — catalog + pipeline split**
1. Core `IPromptCatalog` / `PromptDefinition` (id, display name,
   description, version, system prompt, output kind). Two built-in
   entries (**tres desde 2026-08-26**, ver la nota al final de T8):
   - `assignment-meeting` @ v1 — original structured `MeetingReport`.
   - `functional-spec` @ **v2** — user-supplied technical-analyst
     prompt. Output is Markdown in the **same language as the
     transcript**. Six sections: executive summary, entities/states,
     flow diagram (text or Mermaid), business rules, pending points,
     agreed actions. Not bound to any product domain. An earlier v1
     JSON schema / sample-domain wording was replaced the same day.
2. `ILlmReportExtractor.ExtractAsync` takes an optional `promptId`
   and returns `ExtractionResult` (Markdown body + metadata + prompt,
   plus `MeetingReport` only for the assignment prompt).
3. RecordPage stops at the transcript:
   `StopRecordingAndTranscribeAsync` / `TranscribeAudioFileAsync` /
   `ExtractAndSaveAsync`. Tray / HTTP / harness-record still run the
   full pipeline with the default prompt.
4. Vault save: `Storage:VaultPath` + `Storage:SubFolder`
   (`MeetingReports`). Frontmatter includes `prompt-id`. File prefix
   follows the prompt (`meeting-report-…` / `functional-spec-…`).
5. RecordPage: catalog ComboBox, prompt preview, "Generar reporte".
   Same transcript can be re-run with a different prompt.
6. Harness: `--extract-transcript <file> [--prompt <id>]` and
   `--verify-render`.

**T8.1 — attach existing transcript**
- RecordPage button **Adjuntar transcripción (.txt)**. Loads the file
  into `LastTranscript` (no Deepgram) and reuses the catalog/generate
  path. Rejects non-`.txt` and empty files. Disabled while recording
  or processing. Audio import was renamed **Procesar audio existente**.

**T8.2 — vault path visible**
- Reports were already written to the configured Obsidian vault, under
  `MeetingReports` (not the vault root — easy to miss). RecordPage now
  labels **Guardado en el vault de Obsidian**, shows the full path, and
  has **Mostrar en el Explorador** (`explorer.exe /select`).

**T8.3 — rendered Markdown tab**
- Report area is a `TabView`: **Vista previa** (default) and
  **Markdown** (raw). Preview = Markdig **1.3.2** → HTML in `WebView2`.
  `UseAdvancedExtensions` + `DisableHtml`. Theme follows the app
  (light/dark). YAML frontmatter is stripped before render. Markdig is
  referenced only from `MeetingAssistant.App` — Core still has zero
  package references. If WebView2 fails to start, the error is shown
  and the raw tab still works.

### Acceptance criteria (all of T8–T8.3)
- After record / process-audio / attach-`.txt`, the transcript is on
  screen and the LLM is **not** called until the user picks a prompt
  and clicks generate.
- Catalog lists both built-in prompts; selecting one shows the full
  system prompt. Generate writes a new vault file (does not overwrite)
  and refreshes the on-screen report.
- Generated file lands in `{VaultPath}/MeetingReports/` with
  `prompt-id` in the frontmatter. The path is visible in the UI;
  Explorer can select that file.
- Report viewer has two tabs: rendered Markdown and raw Markdown.
- Tray / HTTP stop still auto-extract with `assignment-meeting`.
- `MeetingAssistant.Core.csproj` still has zero package references.
- `dotnet build MeetingAssistant.sln` succeeds.

### Validation notes (2026-08-14)
- `dotnet build MeetingAssistant.sln` — 0 errors, 0 warnings after
  each increment (catalog, v2 prompt, attach `.txt`, Explorer button,
  TabView + Markdig). Final App build with Markdig 1.3.2: 0/0.
- `MeetingAssistant.Core.csproj` still has zero package references.
  Markdig lives only in `MeetingAssistant.App.csproj`.
- `--verify-render` lists `assignment-meeting @v1` and
  `functional-spec @v2`, and asserts the v2 prompt contains the six
  user-specified sections and does not name an external product.
- Real LLM extracts via harness `--extract-transcript` +
  `--prompt functional-spec` produced vault files
  `functional-spec-20260814-165821.md` (v1 prompt, later superseded)
  and `functional-spec-20260814-175515.md` (v2, Spanish output
  matching a Spanish transcript, six requested sections).
- A later UI generate wrote
  `functional-spec-20260814-180734.md` into the configured vault's
  `MeetingReports` folder (confirmed on disk). The preview in the app
  is a copy of that file, not a substitute for it.
- `dotnet run --project src/MeetingAssistant.App` reached
  "Launching packaged application..." and stayed up. Tab click-through
  of Vista previa / Markdown was confirmed by the user ("Pretty well");
  no GUI automation from this session.

### Follow-ups left open (T8)
- Mermaid diagrams in a functional-spec report render as fenced code
  in the preview (Markdig does not execute Mermaid). Not requested.
- Settings still has no UI to edit `Storage:VaultPath` / `SubFolder`.
- T2.1 (stale RecordPage after tray/HTTP start) is unchanged and
  still blocks T3.

### T8.4 — tercer prompt del catalogo (2026-08-26, commit `f4ae7d4`)

Agregado por el usuario fuera del pase de T6b, anotado acá porque el catálogo es
de T8 y el grafo seguía diciendo "two built-in entries".

- **`feature-handoff` @ v1** — "Handoff de feature (tech lead)". Extrae de la
  llamada con el tech lead: resumen de la feature, requisitos técnicos con sus
  restricciones, alcance dentro/fuera, riesgos y preguntas abiertas, pasos de
  implementación sugeridos y criterios de aceptación. Instruye explícitamente a
  **no inventar requisitos**: lo ambiguo va a "riesgos abiertos" en vez de
  adivinarse. `OutputKind` es `FunctionalSpecification`, o sea Markdown directo,
  no `MeetingReport` estructurado.
- **`MarkdownReportStorage` dejó de ramificar a dos manos.** Antes decidía el
  frontmatter con un ternario `PromptId == FunctionalSpecPrompt.Id`, que hubiera
  etiquetado el prompt nuevo como `meeting-report`. Ahora el `type:` se deriva
  del `PromptId` (`meeting-report` sólo para el prompt por defecto o si viene
  vacío), así que **un cuarto prompt ya no requiere tocar el storage**.
- Verificado el 2026-08-26: compila con 0 warnings / 0 errores y
  `MeetingAssistant.Core.csproj` **sigue sin una sola referencia a paquetes de
  proveedor** — la regla de arquitectura de `AGENTS.md` se mantiene.
- **No ejercitado end-to-end.** El usuario reportó haberlo usado con buen
  resultado, pero la medición contra el vault no lo respaldó (ver la nota de
  cierre de T6b): el reporte más nuevo es `type: functional-spec`. **Ningún
  reporte `feature-handoff` existe todavía en el vault.**

---

## Explicitly out of scope for this task graph

- Building out `HistoryPage` / `SettingsPage` beyond the one autostart toggle
  (T5) — that's Fase 2, already noted as an existing gap, not Fase 3's job.
- Deepgram Keyterm Prompting fix for mixed ES/EN jargon (Fase 0's deferred
  known limitation) — unrelated to Fase 3.
- Store submission / public distribution of the MSIX package — the roadmap
  scope is "Personal, independiente."

## Open questions for the user

- ~~What hotkey combination should be the default for T3?~~ Resolved
  2026-08-21: `Ctrl+Alt+F9`, selected by the user after real testing.
- ~~Should "Salir" from the tray while a recording is in progress block with
  a confirmation dialog, or auto-stop-and-process before exiting?~~ Resolved
  in T2: confirmation dialog, discard-on-confirm (see T2 step 4).
