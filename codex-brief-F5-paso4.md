# Brief para Fase 5, paso 4 — el pipeline empieza a escribir en la base

Generado el 2026-08-27, después del commit `419949a` (ya en `origin/main`). El
plan de la fase vive en `roadmap-meeting-ai-assistant.md`, sección
`## Fase 5 — Persistencia local, historial y búsqueda`; este archivo es la orden
de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir código lee `AGENTS.md` completo y la sección de Fase 5 del
roadmap — en particular **las cinco reglas de diseño**, que ya están decididas y
no se re-discuten acá.

**Los pasos 1, 2 y 3 están hechos y verificados.** Ya existen, funcionando y
probados contra el paquete instalado:

- `SqliteConnectionFactory` y `SqliteSchemaMigrator` (esquema v1, migración
  idempotente).
- `IMeetingHistoryStore` / `ISettingsStore` / `ISecretProtector` en Core, con
  `SqliteMeetingHistoryStore`, `SqliteSettingsStore` y `DpapiSecretProtector` en
  Infrastructure.
- Los tres registrados en el contenedor de DI de `App.xaml.cs`.
- `--verify-db` y `--verify-db-selftest` en el harness. **29 de 29 en verde.**

**Nada de eso escribe todavía una sola fila en producción.** La base existe, está
migrada y está vacía. Eso es exactamente lo que hace este paso.

---

## El trabajo

Que el pipeline registre en la base cada sesión, transcript y reporte, **además**
de seguir escribiendo el `.md` en el vault.

### Regla que manda sobre todas las demás

**El vault sigue siendo el producto y `MarkdownReportStorage` no se toca.**
Obsidian es donde el usuario lee de verdad sus reportes. La base es sistema de
registro e índice; el `.md` es la exportación, y por eso `report.vault_path`
guarda dónde quedó. Si al terminar este paso un reporte deja de aparecer en el
vault, el paso está mal hecho aunque la base esté perfecta.

### Dónde engancharlo

Mirá primero `RecordingCoordinator` y `MeetingPipeline` antes de decidir. Los dos
son candidatos y **no da lo mismo**:

- `MeetingPipeline` está en `Core` y sólo compone abstracciones —
  `IMeetingHistoryStore` es una abstracción de Core, así que meterlo ahí **no
  rompe la regla de arquitectura**. Cubre todos los caminos de una vez.
- `RecordingCoordinator` está en `App` y ya levanta los eventos del ciclo.

Elegí uno y **dejá escrito por qué** en las notas. Lo que no se vale es
engancharlo en los dos y duplicar filas.

**Ojo con el hueco conocido:** `LocalRecordingApiServer` llama a
`IMeetingPipeline` **directo**, sin pasar por `RecordingCoordinator`. Si
enganchás en el coordinador, **las grabaciones por HTTP no quedan registradas** —
y ese es justo el camino que más se usa sin ventana. Es un dato para decidir, no
un detalle.

### Qué tiene que quedar registrado

1. **La sesión se crea al empezar a grabar**, no al final. Está así en la
   interfaz a propósito: si el pipeline revienta a mitad, tiene que quedar
   constancia de que la reunión existió. Hoy no queda nada.
2. `CompleteSessionAsync` con el audio y la duración reales al detener.
3. El transcript en cuanto existe — **antes** de extraer el reporte. Si la
   extracción falla, el transcript no se puede perder: es lo único que no se
   puede regenerar sin volver a pagar Deepgram.
4. Un `NewReport` por cada reporte guardado, con `vault_path` apuntando al `.md`.
   Acordate de que **una sesión admite varios reportes**: el catálogo permite
   re-correr el mismo transcript con otro prompt.
5. `source` con el valor correcto de `SessionSource` (`hotkey`, `tray`, `http`,
   `window`, `import`). No lo dejes siempre en el mismo: el sentido de la columna
   es poder distinguirlos después.

### Lo que no puede pasar

**Un fallo de la base no puede tumbar una grabación.** Es la misma regla que ya
se aplicó al migrador en el arranque, y por el mismo precedente: en T4.4 una
excepción se llevó puesta la app entera y costó nueve días encontrarla.

Si escribir en la base falla, la grabación tiene que **seguir**: transcribir,
extraer y guardar en el vault. Se pierde el historial de esa reunión, no la
reunión. Registralo con `App.LogStartupFailure` (que sirve para cualquier fallo,
no sólo los de arranque) y seguí.

Es un requisito verificable, no una intención: **probalo de verdad**, por ejemplo
apuntando la base a una ruta imposible, y confirmá que la grabación llega igual
al vault.

### `structured_json`

Sólo `assignment-meeting` produce un `MeetingReport` estructurado; el resto del
catálogo devuelve Markdown suelto y va en `null`. Serializalo con
`MeetingReportParser.SerializerOptions`, que ya existe, para que el JSON guardado
tenga la misma forma que el que produce el LLM.

---

## Verificación — esto no se cierra leyendo el código

`AGENTS.md` lo pide y en esta fase se viene cumpliendo: **build + corrida real**.

1. `dotnet build MeetingAssistant.sln` en **0 warnings, 0 errores**. El proyecto
   está en cero y se queda en cero.
2. `dotnet run --project src/MeetingAssistant.Harness -- --verify-db-selftest`
   sigue en verde. Si tocaste el esquema, **agregá una migración v2** — no edites
   el v1: hay bases con v1 ya aplicado, y editarlo hace que el archivo y el
   código dejen de coincidir sin que nada avise.
3. **Contra el paquete instalado, no con `dotnet run`.** Para iterar,
   **subí el `Version` del `Package.appxmanifest`** (hoy `1.0.1.0`) y
   `Add-AppxPackage`: hace la actualización en sitio, no hace falta desinstalar,
   y **el autostart sobrevive**. Desinstalar lo resetea a `Disabled` — pasó
   cuatro veces en un día antes de descubrir esto.
4. Grabá de verdad (el hotkey `Ctrl+Alt+F9` alcanza) y después corré
   `--verify-db`: tienen que aparecer las filas, y los conteos dejar de ser 0.
5. Confirmá que **el `.md` sigue llegando al vault**. Es la regla que manda.
6. Provocá el fallo de base y confirmá que la grabación igual termina en el
   vault.

**Truco útil, ya probado:** para conseguir habla real sin hablar, reproducí por
el altavoz un `.wav` de
`%LOCALAPPDATA%\MeetingAssistant\meeting-output\` mientras grabás — el loopback
lo captura y Deepgram devuelve transcript de verdad. Una grabación en silencio
muere en el guard de "transcripción vacía" y no llega a guardar nada.

---

## No lo arregles aquí

- **El hueco de `LocalRecordingApiServer` → `IMeetingPipeline`.** Tenelo en
  cuenta para decidir dónde enganchar, pero **no lo arregles en este paso**.
- `HistoryPage`, la vista de detalle y la búsqueda: son los pasos 6 y 7.
- El `SqliteConfigurationProvider` y migrar el `appsettings.json` de usuario a la
  base: es el paso 5.
- La política de retención de `meeting-output\`. Backlog.
- El desajuste de husos en los **nombres de archivo** (`.wav` en local, reporte
  en UTC). En la base ya se guarda todo en UTC; renombrar archivos es otra cosa y
  no es de este paso.

---

## Cierre

Actualizá el paso 4 de la sección de Fase 5 en
`roadmap-meeting-ai-assistant.md` con lo que quedó hecho y **lo que no se pudo
verificar, si algo queda así**. En este proyecto el valor de los documentos es
que se les pueda creer: un item dado por bueno sin medirlo vale menos que uno
marcado como no verificado.

Commiteá en `main`. **Podés hacer push**: el usuario ya está trabajando con el
repo sincronizado. Antes de pushear, comprobá que no se cuela nada — el
`appsettings.json` real, `.pfx`/`.cer`, la ruta del vault o el nombre del
empleador. Hay un incidente previo por eso, documentado en `AGENTS.md`.
