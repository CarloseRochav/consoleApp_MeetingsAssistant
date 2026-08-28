# Brief para Fase 5, paso 6 — `HistoryPage` de verdad

Generado el 2026-08-28, después del commit `6f3f6c1` (paso 5 cerrado). El plan de
la fase vive en `roadmap-meeting-ai-assistant.md`, sección
`## Fase 5 — Persistencia local, historial y búsqueda`; este archivo es la orden
de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir código lee `AGENTS.md` completo y la sección de Fase 5 del
roadmap — en particular **las cinco reglas de diseño**, que ya están decididas y
no se re-discuten acá.

**Los pasos 1 a 5 están hechos y verificados.** Ya existen, funcionando y
probados:

- Esquema v1 migrado, `SqliteConnectionFactory` + `SqliteSchemaMigrator`.
- `IMeetingHistoryStore` con **el lado de lectura completo** —
  `ListSessionsAsync(limit, offset)`, `GetSessionAsync`, `GetTranscriptAsync`,
  `GetReportsAsync`, `SearchTranscriptsAsync`, `GetCostSummaryAsync`,
  `GetPromptUsageAsync` — y `SqliteMeetingHistoryStore` detrás.
- El pipeline **ya escribe** sesión, transcript y reporte en cada camino
  (hotkey, bandeja, HTTP, ventana, importación).
- La configuración vive en la base, con las API keys cifradas con DPAPI.
- Autotests en verde: `--verify-db-selftest` 29/29,
  `--verify-settings-config` 33/33, `--verify-render` OK.
- Diagnóstico de la base real: `--verify-db` (sólo lectura; lista sesiones y
  ajustes) y `--set-setting <clave> [valor]` (escribe o borra un ajuste).

**Lo que falta es la página.** `HistoryPage.xaml` sigue siendo el stub de 11
líneas que dice "Próximamente". Y ojo con la conclusión fácil: durante meses se
dijo que el trabajo detrás de la página era el lado de lectura, y eso ya está
hecho — pero **este paso no es "sólo XAML"**. Lee la sección "La trampa" antes de
estimar nada.

---

## El trabajo

Tres cosas, y la tercera es la que le da sentido a haber guardado los
transcripts:

1. **Lista de reuniones pasadas.** Un `ListView` virtualizado sobre
   `ListSessionsAsync`.
2. **Detalle de una reunión**: su transcript y sus reportes, leídos de la base.
3. **Re-generar un reporte desde un transcript viejo con otro prompt.** Hoy es
   imposible porque el transcript no se guardaba. Ya se guarda.

### La regla que manda sobre todas las demás

**El vault sigue siendo el producto.** Una re-extracción tiene que escribir su
`.md` en el vault igual que cualquier otro reporte — no "sólo en la base". Si al
terminar este paso una re-extracción no aparece en Obsidian, el paso está mal
hecho aunque la página quede preciosa.

No hay riesgo de pisar un archivo: `MarkdownReportStorage` nombra
`{prompt-id}-{yyyyMMdd-HHmmss}.md` con la hora de generación, así que
re-extraer la misma sesión produce un archivo nuevo al lado del viejo. Eso es
deseable: es lo que permite comparar dos prompts sobre la misma reunión abriendo
los dos.

---

## La trampa: `_currentSessionId`

**Lee esto antes de tocar la re-extracción.** Es el defecto que este paso va a
pisar si nadie lo mira, y no se ve leyendo `HistoryPage`.

`MeetingPipeline` guarda la sesión en curso en un campo de instancia,
`_currentSessionId`, y `IMeetingPipeline` **está registrado como singleton**
(`App.xaml.cs`). `ExtractAndSaveAsync(transcript, promptId)` usa ese campo para
decidir a qué sesión pertenece el reporte que va a registrar:

- Si el usuario grabó algo en esta sesión de la app, `_currentSessionId` apunta a
  **esa** grabación. Re-extraer una reunión de la semana pasada desde el
  historial dejaría el reporte colgado de la reunión de hoy. Silenciosamente: la
  base queda consistente, el `.md` llega al vault, y el historial miente.
- Si el usuario no grabó nada todavía, `_currentSessionId` es `null` y
  `ExtractAndSaveAsync` **abre una sesión nueva** marcada como
  `SessionSource.Import` — pensada para el transcript pegado a mano de T8. Desde
  el historial, eso crea una reunión fantasma duplicada de una que ya existe.

Ninguna de las dos es aceptable. **La re-extracción necesita decir a qué sesión
pertenece, explícitamente**, en vez de heredar el estado mutable de la última
grabación.

La forma exacta la eliges tú, pero deja escrito por qué. Lo que se espera es una
entrada nueva y explícita en `IMeetingPipeline` — algo como
`ExtractForSessionAsync(long sessionId, string transcript, string promptId)` — y
que `ExtractAndSaveAsync` se quede para el flujo de la ventana, sin cambios de
comportamiento. Lo que **no** se vale es pasar el `sessionId` por un campo, un
`SetCurrentSession`, o cualquier cosa que dependa del orden de las llamadas: el
pipeline es singleton y este bug ya se pagó una vez con este mismo campo.

Y mientras estés ahí, mira si `_currentSessionId` como estado de un singleton
tiene otros filos — dos ventanas de la app no existen (instancia única, T4.1),
pero una grabación por HTTP concurrente con una re-extracción desde la página sí
es posible. **Si decides que es un problema, arréglalo o anótalo; no lo dejes sin
mirar.**

---

## La lista

- `ListSessionsAsync(limit, offset)` ya devuelve `SessionSummary` con
  `StartedAtUtc`, `Duration`, `Source`, `ReportCount`, `TotalCostUsd` y
  `TranscriptPreview`. Es una proyección, no la sesión entera: la lista no tiene
  que traer transcripts completos para pintar filas.
- **Las fechas están en UTC en la base, a propósito.** Convierte a hora local
  **sólo al mostrar** (`.ToLocalTime()`). Esto no es un detalle de estilo: existe
  un desajuste conocido y documentado — el `.wav` se nombra en hora local y el
  reporte en UTC, así que una reunión de la noche aparece con fecha del día
  siguiente al ordenar por nombre. La base es la oportunidad de no repetirlo.
- `Source` viene como la cadena cruda (`hotkey`, `tray`, `http`, `window`,
  `import`, `harness`). Muéstralo legible, pero **no lo esconde**: saber si una
  reunión la disparó el hotkey o el endpoint HTTP es la primera cosa que uno
  quiere ver, y es la razón por la que existe la columna.
- Hay sesiones **sin transcript y sin reportes**: se crean al empezar a grabar, y
  una grabación que falló a mitad deja la fila sola. Eso es deliberado — es el
  rastro de que la reunión existió. La lista tiene que mostrarlas como lo que
  son, no filtrarlas ni pintarlas como si estuvieran completas. Hoy mismo, en la
  base real, hay **6 sesiones y 1 transcript**: el caso no es hipotético, es el
  estado actual.
- El `offset` está en la firma para paginar. Úsalo o no, pero si cargas todo de
  una vez, que sea una decisión y no un olvido.

## El detalle

- **Renderiza el Markdown que está en la base, no el archivo del vault.**
  `report.markdown` es el registro; `report.vault_path` es dónde quedó la
  exportación, y **puede no existir**: el usuario mueve, renombra o borra
  archivos en su vault, que para eso es suyo. "Abrir en el vault" es un extra
  best-effort — si el archivo no está, la página tiene que seguir mostrando el
  reporte. `RecordViewModel.HasSavedReport` ya usa `File.Exists` con ese mismo
  criterio; imítalo.
- Para pintar el Markdown ya existe `MarkdownPreviewRenderer.ToHtmlDocument(md,
  darkTheme)` sobre un `WebView2`. Copia el patrón de `RecordPage.xaml.cs`
  completo, no a medias: `EnsureCoreWebView2Async()` dentro de un `try`, el
  `ActualThemeChanged` que vuelve a renderizar, y el guard de
  `CoreWebView2 is null`. Sin ese guard la vista revienta al navegar rápido.
- **Una sesión tiene varios reportes** (es el sentido del esquema). El detalle
  tiene que dejar ver todos y de qué prompt y versión salió cada uno —
  `prompt_id` y `prompt_version` están en la fila. Eso es lo que convierte
  "comparar calidad entre versiones de prompt" de Fase 4 en algo que se mira en
  vez de recordarse.
- El transcript puede ser largo (miles de caracteres). Que no rompa el layout ni
  se cargue entero en un `TextBlock` sin scroll.

## La re-extracción

- Elegir prompt del `IPromptCatalog` — el mismo catálogo que ya usa `RecordPage`,
  incluido el texto del prompt visible antes de generar.
- **Cuesta dinero de verdad.** Es una llamada al LLM: el usuario tiene que saber
  que va a gastar antes de pulsar, y el resultado tiene que mostrar el costo
  (`Metadata.EstimatedCostUsd` ya viene en el `ExtractionResult`).
- Deja el botón inhabilitado mientras corre y mientras haya una grabación en
  curso. `RecordViewModel` ya tiene esa lógica (`CanGenerateReport`,
  `IsProcessing`, `IsRecording`) y `RecordingCoordinator` ya expone el estado:
  úsalo, no inventes un segundo semáforo.
- Al terminar: fila nueva en `report`, `.md` nuevo en el vault, y la lista y el
  detalle reflejándolo sin tener que reiniciar la app.
- Una sesión **sin transcript** no se puede re-extraer. Que el botón lo diga, no
  que falle al pulsarlo.

---

## Lo que no puede pasar

**Un fallo de la base no puede tumbar la app ni la página.** Es la regla que
viene rigiendo toda la fase, y el precedente sigue siendo T4.4 — una excepción de
arranque se llevó puesta la app entera y costó nueve días encontrarla. Acá el
alcance es menor pero el criterio es el mismo: si la base no abre o una consulta
revienta, **la página muestra un mensaje y el resto de la app sigue funcionando**
(grabar, transcribir y guardar en el vault no dependen del historial). Registra
el fallo con `App.LogStartupFailure`, que sirve para cualquier fallo y no sólo
los de arranque.

Pruébalo de verdad, no lo dejes en intención: la forma barata es apuntar la
fábrica de conexiones a una ruta imposible, como ya hacen dos de los autotests.

**El estado vacío también es un caso.** Una instalación nueva no tiene sesiones.
"Próximamente" era un stub; una tabla vacía sin explicación es peor. Que diga qué
hacer para que aparezca algo.

---

## Cómo se construyen las páginas en este proyecto

No inventes un patrón nuevo:

- Las páginas tienen **constructor sin parámetros** y resuelven sus dependencias
  con `App.Services.GetRequiredService<T>()` (mira `RecordPage`, `SettingsPage`).
- `MainWindow.xaml.cs` navega con `ContentFrame.Navigate(typeof(HistoryPage))`,
  **sin parámetro**, y la página se reconstruye en cada navegación. O sea: carga
  la lista en `Loaded`, no en el constructor, y no asumas que el estado sobrevive
  a salir y volver a entrar.
- **El botón de retroceso del `NavigationView` está en
  `IsBackButtonVisible="Collapsed"`.** Si eliges una página de detalle separada,
  tienes que resolver la navegación de vuelta tú. Un maestro-detalle dentro de
  `HistoryPage` evita ese trabajo entero; si vas por la página aparte, deja
  escrito por qué valía la pena.
- MVVM con `CommunityToolkit.Mvvm`, como `RecordViewModel`. Un
  `HistoryViewModel` registrado en el contenedor es lo esperable.
- La regla de arquitectura no se toca: si necesitas algo nuevo en Core, que sea
  una interfaz o un modelo. `Core.csproj` tiene **0 `PackageReference`** y se
  queda así — compruébalo antes de dar el paso por terminado.

---

## Verificación — esto no se cierra leyendo el código

`AGENTS.md` lo pide y en esta fase se viene cumpliendo: **build + corrida real**.

1. `dotnet build MeetingAssistant.sln` en **0 warnings, 0 errores**. El proyecto
   está en cero y se queda en cero.
2. Los tres autotests siguen en verde: `--verify-db-selftest`,
   `--verify-settings-config`, `--verify-render`. Si tocas el esquema,
   **agrega una migración v2** — no edites el v1: hay bases con v1 aplicado, y
   editarlo hace que el archivo y el código dejen de coincidir sin que nada
   avise.
3. **Usa `dotnet run --project src/MeetingAssistant.App` para probar en la app
   real.** `dotnet build` **no** refresca el layout `AppX\` que ejecuta el
   paquete registrado, así que lanzar por AUMID después de compilar corre el
   binario viejo sin ningún aviso. Esto ya costó una vuelta de depuración el
   2026-08-28 y está anotado en `AGENTS.md`.
4. **En pantalla, con la base real** (hoy: 6 sesiones, 1 transcript, 0 reportes):
   - la lista trae las sesiones, con fecha local y su origen;
   - una sesión sin transcript se ve como tal y no revienta al abrirla;
   - el detalle de la sesión que **sí** tiene transcript lo muestra;
   - una re-extracción con otro prompt deja fila nueva en `report` — confírmalo
     con `--verify-db`, que los conteos dejen de ser 0 — **y `.md` nuevo en el
     vault**;
   - el reporte re-extraído queda colgado de **la sesión correcta**. Esta es la
     comprobación que justifica la sección "La trampa": grabá algo primero, y
     después re-extraé una sesión vieja. Si el reporte aparece bajo la grabación
     de recién, el bug está ahí.
5. Provoca el fallo de base y confirma que la app sigue usable.
6. **De paso, cierra el pendiente del paso 5:** pulsa **Guardar** en
   Configuración y confirma con `--verify-db` que los ajustes se escribieron y
   que las API keys siguen apareciendo como `cifrado, descifrable`. Es la única
   cosa del paso 5 que quedó sin verificar — requiere GUI, y este paso es el
   primero que la abre. Vacía un campo no crítico (p. ej. `Gemini:Model`) y
   confirma que la clave **desaparece** de la base en vez de quedar vacía: así es
   como se vuelve al valor empaquetado.

**Truco útil, ya probado:** para conseguir habla real sin hablar, reproduce por
el altavoz un `.wav` de `%LOCALAPPDATA%\MeetingAssistant\meeting-output\`
mientras grabas — el loopback lo captura y Deepgram devuelve transcript de
verdad. Una grabación en silencio muere en el guard de "transcripción vacía".

**Y si la grabación falla con un error que parece de transcripción:** revisa el
consentimiento de micrófono. Desinstalar el paquete lo resetea a `Deny`, el error
aparece al **detener** y no al iniciar, y por el camino HTTP no deja ninguna
línea en el log. Escribir `Allow` en el registro **no alcanza** — hay que
activarlo desde *Configuración > Privacidad y seguridad > Micrófono*. Está
documentado en el paso 4 del roadmap.

---

## No lo arregles aquí

- **La búsqueda full-text.** `SearchTranscriptsAsync` ya existe y funciona, y va
  a ser tentador ponerle una caja de búsqueda a la lista. Es el **paso 7**.
  Si dejas el lugar preparado, que se note que está vacío a propósito.
- **La vista de costo acumulado y la comparación entre prompts.**
  `GetCostSummaryAsync` y `GetPromptUsageAsync` ya existen. Es el **paso 8**.
  Mostrar el costo *de un reporte* en su detalle sí es de este paso; el
  acumulado, no.
- **Editar el prompt desde Configuración.** El catálogo sigue siendo de sólo
  lectura, definido en código. Es deuda de Fase 2, no de este paso.
- **El hueco de `LocalRecordingApiServer` → `IMeetingPipeline`** (no pasa por
  `RecordingCoordinator`, así que no actualiza la UI ni dispara toasts). Backlog
  de Fase 4.
- **La política de retención de `meeting-output\`.** Backlog.
- **Vaciar el `appsettings.json` empaquetado**, que sigue con credenciales en
  claro dentro del `.msix` de sólo lectura. Es una decisión aparte, anotada al
  cerrar el paso 5.

---

## Cierre

Actualiza el paso 6 de la sección de Fase 5 en
`roadmap-meeting-ai-assistant.md` con lo que quedó hecho y **lo que no se pudo
verificar, si algo queda así**. En este proyecto el valor de los documentos es
que se les pueda creer: un item dado por bueno sin medirlo vale menos que uno
marcado como no verificado.

Si arreglas el defecto de `_currentSessionId`, cuéntalo como lo que es — un
defecto encontrado, no una mejora — y di cómo lo comprobaste. El paso 4 dejó
precedente: el test encontró que el reporte no se registraba en
`ExtractSessionAsync`, algo que la lectura de código no había visto.

Commitea en `main`. **Puedes hacer push**: el usuario ya está trabajando con el
repo sincronizado. Antes de pushear, comprueba que no se cuela nada — el
`appsettings.json` real, `.pfx`/`.cer`, la ruta del vault o el nombre del
empleador. Hay un incidente previo por eso, documentado en `AGENTS.md`.
