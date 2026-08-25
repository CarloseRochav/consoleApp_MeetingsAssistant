# Brief para Codex — T6a (identidad de paquete + firma)

Generado el 2026-08-25, despues del commit `0de4135`. El plan detallado sigue
viviendo en `TASK_GRAPH.md`; este archivo es la orden de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`).

Antes de escribir codigo lee `AGENTS.md` completo, la seccion `## T6 — MSIX
signing and local persistent install` de `TASK_GRAPH.md` (T6a son sus pasos 1-3)
y la seccion `## Orden de cierre de Fase 3 (replanificado 2026-08-22)`. Las
reglas de `AGENTS.md` son obligatorias: `MeetingAssistant.Core` no gana
referencias de plataforma ni de proveedor, el `appsettings.json` real no se
commitea, y nada se marca como hecho sin haberlo corrido de verdad.

## Estado del que partis

T4 quedo con su criterio critico cumplido: el 2026-08-24 el usuario confirmo en
pantalla el toast de fallo con la ventana cerrada. Lo que **sigue abierto de T4**
y no es tuyo — no lo cierres desde aca, se cierra en el mismo pase de GUI o en
T6b:

- Los toasts de `TranscriptReady` y `ReportSaved`: el camino de exito nunca se
  ejercito porque la transcripcion vino vacia.
- El clic real en un toast de punta a punta (T4.1).
- El label de la bandeja despues de un `POST /recording/start` (T2.2).

## Por que esta tarea va antes que T5

T5 declara un `StartupTask`, y `RequestEnableAsync()` mas los estados
`DisabledByUser`/`DisabledByPolicy` solo se comportan de verdad con identidad de
paquete real. Sin T6a, T5 no se puede validar — solo escribir. Ese es el motivo
del reordenamiento del 2026-08-22.

## Paso 0, antes de generar cualquier certificado: el `.gitignore`

Verificado con `git check-ignore -v` el 2026-08-25, porque el `TASK_GRAPH.md`
dice esto de forma imprecisa y la imprecision importa. **Hay dos `.gitignore`**:

| Ruta candidata | Estado real |
|---|---|
| `src/MeetingAssistant.App/AppPackages/x.msix` | ignorado (`src/MeetingAssistant.App/.gitignore:33`) |
| `src/MeetingAssistant.App/Properties/PublishProfiles/win-x64.pubxml` | ignorado (linea 43) |
| `AppPackages/y.appx` en la **raiz** del repo | **RASTREABLE** |
| `cert.pfx` en la raiz **o** en el dir de la App | **RASTREABLE** |

O sea: la cobertura de empaquetado existe pero **solo bajo
`src/MeetingAssistant.App/`**. El `.gitignore` de la raiz no tiene ni una linea
de packaging. Consecuencias practicas:

1. Agrega `*.pfx` (y de paso `*.cer`, `*.snk`) al `.gitignore` de la **raiz**
   antes de generar nada. Hoy un `.pfx` es rastreable en cualquier ubicacion.
2. Si la salida del empaquetado cae fuera de `src/MeetingAssistant.App/` —
   porque usaste `AppxPackageDir` o un perfil que apunta a otro lado — deja de
   estar ignorada. Mantenela bajo el dir del proyecto, o extende el
   `.gitignore` de la raiz.
3. Preferible igual: guarda el `.pfx` **fuera del repo**. El gitignore es la red
   de seguridad, no el plan.

## Implementacion

1. **`Identity/Publisher` debe coincidir exacto con el Subject del
   certificado.** Hoy es el placeholder `CN=AppPublisher`
   (`Package.appxmanifest:15`). Si no coinciden caracter por caracter, la firma
   falla. Deci el Subject primero y despues escribi el manifiesto, no al revés.
2. `PublisherDisplayName` es `AppPublisher` y `DisplayName` es
   `MeetingAssistant.App`, los dos placeholders
   (`Package.appxmanifest:22-23` y `uap:VisualElements`). **Arreglalos aca.** No
   es cosmetico: el criterio de aceptacion de T4 pedia que el toast apareciera
   "atribuido correctamente a la app (nombre e icono)", y esos son exactamente
   los campos que Windows muestra en el toast y en Configuracion > Aplicaciones.
   T6a es donde eso pasa a ser real.
3. Certificado self-signed con `New-SelfSignedCertificate` (esta es una
   herramienta personal, sideload — no Store). Documenta en las notas de
   validacion el paso unico de confiar el certificado si hace falta.
4. Genera el `.msix` y **instalalo de verdad** con `Add-AppxPackage`. Producir
   el paquete sin instalarlo no cierra este paso.
5. `Platforms` es `x86;x64;ARM64` y el `RuntimeIdentifier` se resuelve por
   arquitectura del proceso (`MeetingAssistant.App.csproj:8-9`). Empaqueta x64,
   que es lo que se viene corriendo y validando.

## Lo que cambia al cambiar `Publisher`, y que va a romper si no lo tenes presente

El `PackageFamilyName` se deriva de `Identity.Name` **mas un hash del
`Publisher`**. Hoy es `962A0BC5-A1BC-432A-8A38-55011BFE3EE0_1z32rh13vfry6`. Al
cambiar el `Publisher`, el PFN cambia — y con el, tres cosas que este proyecto
ya sufrio:

1. **El log de diagnostico se muda.** Hoy vive en
   `%LOCALAPPDATA%\Packages\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_1z32rh13vfry6\LocalCache\Local\MeetingAssistant\startup-errors.log`.
   Con el PFN nuevo, esa ruta queda muerta y el log real esta en otra. Buscarlo
   en la ruta vieja es exactamente el error que costo dos dias en T4.3.
   **Anota la ruta nueva en las notas de validacion.**
2. **El AUMID cambia**, asi que la clave de diagnostico de toasts
   `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\{AUMID}`
   (`PeriodicNotificationCount`, `LastNotificationAddedTime`) pasa a ser otra. Es
   el metodo que T4.3 dejo documentado para distinguir "no se mostro" de "se
   mostro y no lo vi"; reapuntalo al AUMID nuevo antes de usarlo.
3. **El registro de desarrollo y el MSIX firmado se vuelven dos paquetes
   distintos** y pueden coexistir. Si los dos arrancan: dos iconos de bandeja,
   `RegisterHotKey` que falla, y el bind de 5757 reventando con
   `HttpListenerException 183`. Peor: al tener PFN distinto **`AppInstance` ya no
   los deduplica entre si**, asi que el mecanismo de instancia unica de T4.1 no
   te salva de este choque. **Desinstala el registro de desarrollo antes de
   instalar el firmado**, y verifica que solo quede uno.

## El `.msix` va a contener tus API keys

`MeetingAssistant.App.csproj:73` tiene
`<None Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />`, y
`appsettings.json` esta confirmado presente en el layout `AppX\`. O sea que el
`appsettings.json` **real**, con las keys de Deepgram, Gemini y Azure, se empaqueta
dentro del `.msix`.

No es un defecto a arreglar en T6a — la app necesita su configuracion para
arrancar (`StartupConfigurationValidator.Validate` tira si falta). Pero cambia
como hay que tratar ese archivo:

- El `.msix` **no se comparte con nadie** y no se sube a ningun lado. Es un
  artefacto con secretos, no un instalador distribuible.
- Que no caiga en la raiz del repo, donde no esta ignorado (ver paso 0).
- Rotar una key implica reconstruir el paquete, no editar un archivo.

Si el usuario alguna vez quiere distribuir esto, la configuracion tiene que
salir del paquete y pasar a `%LOCALAPPDATA%` en el primer arranque. Es una tarea
propia, no un parche aca: **mencionalo, no lo implementes.**

## Huecos conocidos, no los arregles aqui

- **`LocalRecordingApiServer` llama a `IMeetingPipeline` directo**, no al
  `RecordingCoordinator`, asi que una grabacion disparada por HTTP no levanta
  `StateChanged` / `RecordingStarted` / `ReportSaved` / `RecordingFailed`: no
  actualiza `RecordPage` ni produce toast. Esta en el backlog del
  `TASK_GRAPH.md`.
- **Hay un segundo checkout del proyecto** en
  `D:\stuffProjectsCH\proyecto-codex-worker`, hoy con identidad de paquete
  **identica** a este repo. Efecto colateral bueno: al cambiar el `Publisher`,
  esa colision desaparece sola. No toques ese directorio.

## Trampa de build, ya mordio tres veces

Con la app corriendo, `dotnet build` reporta exito pero **no actualiza el layout
`AppX\`**: el proceso vivo tiene el `.exe` y el `.dll` tomados. Cerra la app
(bandeja > Salir) **antes** de compilar. El 2026-08-24 el layout estaba 36
minutos atrasado respecto del ultimo build y nadie lo noto.

## Criterios de aceptacion

- `git check-ignore -v` confirma que un `.pfx` esta ignorado en la raiz y en el
  dir del proyecto, **corrido antes** de generar el certificado.
- `Identity/Publisher` y `PublisherDisplayName` sin placeholders, y el
  `Publisher` coincide exacto con el Subject del certificado.
- Un `.msix` firmado, **instalado** con `Add-AppxPackage`, que arranca desde el
  menu Inicio (no `dotnet run`) y muestra el icono de bandeja.
- Solo un paquete registrado: el registro de desarrollo desinstalado, y
  `Get-AppxPackage` mostrando una sola entrada.
- El log de diagnostico localizado en su ruta nueva, con la ruta escrita en las
  notas de validacion, y `AppNotificationManager.Register OK` presente ahi.
- `git status` limpio de certificados: el `.pfx` no aparece ni como untracked.
- `dotnet build MeetingAssistant.sln -t:Rebuild` sin errores. Usa `-t:Rebuild`:
  un build incremental que se salta `Infrastructure` oculta warnings reales.
  Hoy la solucion compila con 0 errores y 0 warnings — si aparece un warning,
  es tuyo.

Lo que **no** es criterio de T6a: re-verificar T2-T5 completos contra la
instalacion. Eso es T6b, y necesita T5 hecho.

## Cierre

Actualiza `TASK_GRAPH.md`: estado de T6a en la tabla de `## Status` y en la
tabla de orden de cierre, y notas de validacion con lo que **realmente**
corriste — incluida la ruta nueva del log y el AUMID nuevo, que el proximo que
depure algo los va a necesitar. Si algo no se pudo probar de verdad, decilo
explicitamente en lugar de rellenarlo.

Despues de T6a sigue T5 (autostart: `Windows.ApplicationModel.StartupTask` mas
**un** toggle en `SettingsPage`, no la pagina completa — `SettingsPage` sigue
siendo un stub de Fase 2 y no es tu tarea construirla).

Commitea en `main` con un mensaje que explique el porque, no solo el que. No
hagas push: el usuario lo revisa antes.
