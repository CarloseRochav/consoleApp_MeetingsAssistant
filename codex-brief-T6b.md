# Brief para T6b — pase de aceptacion final de Fase 3

Generado el 2026-08-25, despues del commit `b259b0a`. El plan detallado sigue
viviendo en `TASK_GRAPH.md`; este archivo es la orden de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir codigo lee `AGENTS.md` completo, la seccion `## T6 — MSIX
signing and local persistent install` de `TASK_GRAPH.md` (T6b es su paso 4 mas
los criterios de aceptacion) y la seccion `## Orden de cierre de Fase 3`.

**T6b es distinto de todo lo anterior: no es una tarea de features, es un pase
de validacion.** La mayor parte hay que verla con los ojos, en la GUI, contra el
paquete instalado. Un agente puede correr los chequeos scriptables, arreglar el
defecto del paso 0 y escribir las notas; **no puede** confirmar que un toast se
dibujo ni que un icono aparecio. Lo que no se vio, se anota como no visto.

---

## Paso 0 — un defecto que bloquea el pase entero

**Grabar no puede funcionar hoy bajo el paquete instalado.** Encontrado y medido
el 2026-08-25, antes de empezar T6b.

`App.xaml.cs:318` construye el pipeline con
`Path.Combine(AppContext.BaseDirectory, "meeting-output")`. Corriendo instalada,
`AppContext.BaseDirectory` es
`C:\Program Files\WindowsApps\962A0BC5-...__n5p1q6rt9wnn4\`, y
`AudioCaptureService.CaptureUntilStoppedAsync` hace
`Directory.CreateDirectory(outputDirectory)` (linea 80) antes de escribir el
`.wav`.

Medido como el usuario, sin elevar:

```
ESCRITURA DENEGADA -> Access to the path
'C:\Program Files\WindowsApps\962A0BC5-..._x64__n5p1q6rt9wnn4\meeting-output-writetest.tmp'
is denied.
```

El directorio `meeting-output` no existe ahi y no se puede crear.

**Como se va a manifestar**, porque no es obvio: `CreateDirectory` corre dentro
del `Task.Run` de `AudioCaptureService.StartAsync` (linea 35), no en el camino
sincronico. Asi que **iniciar** la grabacion parece funcionar — el toast de
`RecordingStarted` sale — y el fallo recien aparece al **detenerla**, cuando se
espera esa tarea. Si no sabes esto, parece un fallo de transcripcion.

**Por que nunca se vio antes:** todas las validaciones de grabacion se hicieron
bajo registro de desarrollo, donde `BaseDirectory` es el layout `AppX\` dentro de
`bin\`, que si es escribible. T6a y T5 no ejercitaron grabacion. O sea: **ninguna
grabacion corrio nunca bajo el paquete instalado.**

### El arreglo

Mover el directorio de audio a `LocalApplicationData`, con el mismo criterio y el
mismo precedente que `App.StartupErrorLogPath`, que ya resolvio exactamente este
problema para el log:

```
%LOCALAPPDATA%\MeetingAssistant\meeting-output
```

No toques `.SetBasePath(AppContext.BaseDirectory)` de la configuracion
(`App.xaml.cs:293`): **leer** de WindowsApps funciona bien, el problema es
escribir. Y no toques `MarkdownReportStorage`, que ya guarda en el vault
configurable por `Storage:VaultPath`, fuera del paquete.

Es un cambio chico pero es codigo, no validacion: **commit propio, separado del
resto de T6b**, con su mensaje explicando por que.

---

## Precondicion, antes del primer chequeo

Verifica que estas validando el paquete **instalado y firmado**, no un registro
de desarrollo:

```
Get-AppxPackage -Name "962A0BC5*" | Select PackageFamilyName,SignatureKind,Status,IsDevelopmentMode
```

Tiene que decir `IsDevelopmentMode=False`. **Un `dotnet run` reemplaza la
instalacion firmada** — desde T6a el manifiesto lleva el Subject del certificado,
o sea la misma identidad. Si arreglaste el paso 0 iterando con `dotnet run`,
reconstrui e **reinstala el MSIX firmado** antes de seguir. Si no, todo el pase
valida lo que no es.

Recorda tambien que la ubicacion del log depende de eso: instalada usa
`%LOCALAPPDATA%\MeetingAssistant\startup-errors.log`; con registro de desarrollo
se va a `%LOCALAPPDATA%\Packages\{PFN}\LocalCache\Local\MeetingAssistant\`.

---

## La lista

Marca cada uno como **visto**, **medido** o **no verificado**. No hay una cuarta
opcion, y "deberia funcionar" no es ninguna de las tres.

### A. Arranque e identidad — scriptable

- [ ] Arranca desde el menu Inicio / AppsFolder, no con `dotnet run`. El proceso
      corre desde `C:\Program Files\WindowsApps\...`.
- [ ] `startup-errors.log` en la ruta plana, con
      `AppNotificationManager.Register OK` y **sin** excepcion de
      `LocalRecordingApiServer.Start`.
- [ ] Un solo paquete registrado.

### B. T2 — bandeja y ventana — GUI

- [ ] El icono de bandeja aparece, con el icono correcto (no el generico).
- [ ] Cerrar la ventana la **oculta**; el proceso sigue vivo y el icono queda.
- [ ] "Salir" desde la bandeja termina el proceso de verdad: no queda proceso
      huerfano ni el puerto 5757 tomado.
- [ ] **T2.2, pendiente desde el 08-21:** con una grabacion iniciada por
      `POST /recording/start`, click derecho en la bandeja muestra "Detener
      grabación", no "Iniciar". Es lo que decide si `RightClickCommand` corre
      antes de que se construya el menu nativo.

### C. T3 — hotkey — GUI

- [ ] `Ctrl+Alt+F9` inicia y detiene con la ventana **cerrada**.
- [ ] Con dos apps compitiendo por el hotkey no aplica: si `RegisterHotKey`
      fallo, tiene que haber quedado en el log.

### D. T4 — los cuatro toasts — GUI, con la ventana cerrada

Dos ya se vieron el 2026-08-24, pero **bajo registro de desarrollo**: hay que
rehacerlos contra la instalacion.

- [ ] `RecordingStarted` al iniciar.
- [ ] `TranscriptReady` cuando hay transcript y falta elegir prompt.
- [ ] `ReportSaved` con la ruta del reporte guardado.
- [ ] `RecordingFailed` — forza uno, por ejemplo con una API key invalida
      temporalmente.
- [ ] Ninguno filtra transcript ni contenido de la reunion en el cuerpo. Ruta y
      nombre de prompt si; contenido no. El historial de notificaciones de
      Windows persiste eso fuera del vault.
- [ ] **Esto necesita una grabacion con habla real** que llegue a guardar
      reporte. Los dos toasts del camino de exito nunca se ejercitaron: el
      intento del 08-24 murio con "transcripcion vacia — no se detecto habla".

Para diagnosticar si un toast no aparece, el contador por AUMID:

```
HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4!App
    PeriodicNotificationCount, LastNotificationAddedTime
```

Distingue "no se mostro" de "se mostro y no lo vi". La clave **todavia no
existe**: ningun toast se entrego aun bajo la identidad nueva, asi que la crea el
primero.

### E. T4.1 — clic en un toast — GUI

- [ ] Clic en un toast con la ventana oculta: la ventana vuelve al frente.
      Nunca se probo de punta a punta.
- [ ] No queda un segundo proceso: la activacion por COM se redirige a la
      instancia viva.

### F. Endpoint HTTP — scriptable

- [ ] `POST http://localhost:5757/recording/start` sin token responde **401**.
- [ ] Con token valido inicia grabacion.
- [ ] **No esperes toast de una grabacion disparada por HTTP.** Es un hueco
      conocido del backlog: `LocalRecordingApiServer` llama a `IMeetingPipeline`
      directo y no levanta los eventos del coordinador. **No lo arregles aca** y
      no lo reportes como defecto nuevo.

### G. T5 — autostart — GUI

- [ ] El toggle enciende y apaga, y se refleja en `Administrador de tareas >
      Aplicaciones de inicio`.
- [ ] Deshabilitado desde el SO, el toggle muestra `DisabledByUser` con su
      explicacion y no un "encendido" enganoso.
- [ ] `DisabledByPolicy` / `EnabledByPolicy` no se pueden provocar sin politicas
      administradas. Estan implementados; anotalos como **no verificables en esta
      maquina**, no como verificados.

### H. Desinstalacion limpia — dejala para el final

**Esto destruye tu instalacion de trabajo.** Corrilo al final y reinstala
despues.

- [ ] Desinstalar desde Configuracion > Aplicaciones quita la app.
- [ ] No queda la StartupTask registrada.
- [ ] No queda proceso de bandeja huerfano ni el puerto 5757 tomado.
- [ ] **El certificado sigue en `LocalMachine\TrustedPeople` y hay que sacarlo a
      mano** — thumbprint `AD5A94D0DA131E47F395DD937721551C72AF5D52`, valido
      hasta 2029-08-25. Configuracion de Windows no lo revierte. Sin este paso,
      "desinstalacion limpia" es falso.
      Precedente directo: la reserva de urlacl de T4.4, que sobrevivio nueve dias
      a la reversion de su codigo y despues rompio el arranque de la app.
- [ ] `%LOCALAPPDATA%\MeetingAssistant\` **sobrevive** a la desinstalacion: ahi
      quedan el log, la carpeta `Signing\` con el `.cer` que exporto T6a, y —
      despues del paso 0 — el `meeting-output` con audio de reuniones. Decidi
      explicitamente si se borra o se conserva, y **anota la decision**; no lo
      dejes sin mirar.

---

## No lo arregles aqui

- El hueco de `LocalRecordingApiServer` -> `IMeetingPipeline`. Backlog.
- `HistoryPage` y `SettingsPage` fuera del toggle de T5: son Fase 2.
- La validacion de configuracion que solo verifica presencia y no que
  `Storage:VaultPath` exista (T7). Backlog.
- El `<com:ExeServer DisplayName="MeetingAssistant.App">` con el nombre viejo.
- El directorio `D:\stuffProjectsCH\proyecto-codex-worker`.

## Cierre

Actualiza `TASK_GRAPH.md`: estado de T6b en la tabla de `## Status` y en la de
orden de cierre, y notas de validacion con **cada item de la lista** y su
resultado real. Si un item no se pudo verificar, decilo — el valor de este
documento es que se pueda confiar en el, y un pase de aceptacion con items
rellenados no vale nada.

Si todos los items pasan, **Fase 3 cierra**. Anotalo tambien en
`roadmap-meeting-ai-assistant.md`, donde el paso 6 (empaquetado MSIX) y el
criterio de salida de Fase 3 siguen sin marcar.

Commitea en `main`. El arreglo del paso 0 va en su propio commit, separado de las
notas de validacion. No hagas push: el usuario lo revisa antes.
