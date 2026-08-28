# Brief para Fase 5, paso 7 — Búsqueda full-text sobre transcripts

Generado el 2026-08-28, después del commit `223382f` (paso 6 cerrado). El plan de
la fase vive en `roadmap-meeting-ai-assistant.md`, sección
`## Fase 5 — Persistencia local, historial y búsqueda`; este archivo es la orden
de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir código lee `AGENTS.md` completo y la sección de Fase 5 del
roadmap — en particular **las cinco reglas de diseño**, que ya están decididas y
no se re-discuten acá.

## Empieza sabiendo esto: el motor ya está hecho y probado

Esto no es como el paso 6, donde "sólo falta el XAML" era una trampa. Acá **sí**
es casi todo interfaz, y conviene decirlo para que no salgas a reconstruir lo que
ya existe:

- `IMeetingHistoryStore.SearchTranscriptsAsync(query, limit)` **existe y
  funciona**, con `SqliteMeetingHistoryStore` detrás
  (`src/MeetingAssistant.Infrastructure/Storage/Sqlite/SqliteMeetingHistoryStore.cs:262`).
  Devuelve `TranscriptSearchHit(SessionId, StartedAtUtc, Snippet)` ordenado por
  `rank` de FTS5.
- El **saneado de la consulta** está resuelto en
  `SqliteValueConversions.ToFts5Query`: cada palabra se entrecomilla como literal
  y se le agrega el operador de prefijo. Se pierde poder de consulta y se gana
  que **nunca lance**.
- La tabla virtual `transcript_fts` y sus tres triggers (insert/update/delete)
  están en el esquema v1, con tokenizador `unicode61 remove_diacritics 2`.
- `--verify-db-selftest` (29/29) ya cubre: el índice se alimenta y se limpia con
  los triggers, `sesion` encuentra `sesión` **y al revés**, la búsqueda por la
  interfaz devuelve fragmento no vacío, el `integrity-check` de FTS5, y —lo que
  más importa— que siete entradas hostiles (comilla suelta, `AND`, `NEAR(`,
  asterisco solo, `a OR`, paréntesis abiertos, apóstrofo) **no lanzan**.

**Tu trabajo es la caja de búsqueda y lo que pasa alrededor de ella.** Si al
terminar tocaste el SQL de búsqueda, que sea porque encontraste algo mal, y
cuéntalo.

---

## La trampa: la lista y los resultados no tienen la misma forma

Léelo antes de tocar `HistoryViewModel`. Es donde este paso se rompe solo.

La página es un maestro-detalle. La lista está poblada de `SessionListItem`,
construidos desde `SessionSummary`, y **todo lo demás cuelga de
`SelectedSession`**: `OnSelectedSessionChanged` carga transcript y reportes,
`CanRegenerate` mira `SelectedSession is not null`, y la re-extracción usa
`session.SessionId`.

`SearchTranscriptsAsync` **no** devuelve eso. Devuelve `TranscriptSearchHit`, que
tiene tres campos: `SessionId`, `StartedAtUtc` y `Snippet`. No trae `Source`, ni
`Duration`, ni `ReportCount`, ni `TotalCostUsd`.

El camino obvio —vaciar `Sessions` y llenarlo con los resultados— cuesta dos
cosas, y la segunda es silenciosa:

1. Las filas pierden origen, duración, conteo de reportes y costo. La lista pasa
   a decir menos justo cuando el usuario está buscando algo concreto.
2. Si además cambias el tipo de la colección, `SelectedSession` deja de ser un
   `SessionListItem` y **se cae toda la cadena del detalle y de la
   re-extracción** — o peor, la arreglas a medias y la re-extracción queda
   colgando de un item incompleto.

**Lo que se espera:** que `SessionListItem` siga siendo el único tipo de fila, y
que la búsqueda **anote o filtre** esa lista en vez de reemplazarla por otra
cosa. Un `Snippet` opcional en el item, poblado sólo cuando hay búsqueda activa,
mantiene el detalle y la re-extracción funcionando sin tocarlos. Si eliges otro
camino, deja escrito por qué y comprueba que re-extraer desde un resultado de
búsqueda sigue funcionando — esa es la prueba que lo delata.

---

## Lo segundo que hay que mirar: la búsqueda no ve casi nada

`transcript_fts` indexa **transcripts**. Una sesión sin transcript no puede
aparecer en ningún resultado, nunca.

En la base real de hoy hay **6 sesiones y 1 transcript**. O sea: cualquier
búsqueda va a devolver como máximo **una** reunión, y las otras cinco van a
desaparecer de la lista. Eso **se ve exactamente igual que un defecto**, y quien
lo mire —el usuario, o tú mismo dentro de dos semanas— va a pensar que la
búsqueda está rota.

No lo escondas ni lo maquilles: dilo en pantalla. "Ninguna de tus reuniones
menciona X" y "sólo 1 de tus 6 reuniones tiene transcripción para buscar" son
mensajes distintos, y el usuario necesita el segundo. El dato ya lo tienes:
`SessionListItem.HasTranscript` está poblado desde `TranscriptPreview`.

Y ojo con el corolario: **el estado vacío de la búsqueda no es el estado vacío de
la página.** Ya existe `IsEmpty` para "no hay historial todavía". Una búsqueda
sin resultados sobre un historial lleno es otra cosa y merece otro texto.

---

## Detalles de comportamiento que hay que decidir a propósito

- **La carrera entre pulsaciones.** Escribiendo se disparan consultas solapadas,
  y una vieja puede volver después de una nueva y pisar los resultados. El patrón
  correcto ya está en el archivo: `LoadSessionDetailAsync` descarta su resultado
  con `if (SelectedSession?.SessionId != sessionId) return;`. Imítalo — y agrega
  un *debounce* para no consultar en cada tecla. La base es local y rápida, así
  que esto no es por rendimiento: es para que lo que queda en pantalla sea el
  resultado de lo que está escrito **ahora**.
- **La búsqueda es por prefijo, y sólo por prefijo.** `factura` encuentra
  `facturación`; `facturación` **no** encuentra `factura`. Los acentos no
  importan en ninguna dirección (probado en los dos sentidos). Vale una línea de
  ayuda en la UI, o al menos un comentario: es el tipo de cosa que se reporta
  como defecto cuando en realidad es la decisión que evita que la caja lance.
- **Varias palabras se combinan con AND implícito** (FTS5 une los términos por
  yuxtaposición). Es lo razonable; sólo tenlo claro antes de prometer otra cosa
  en un texto de ayuda.
- **El orden cambia al buscar.** La lista normal va por fecha; los resultados
  vienen `order by rank`, o sea por relevancia bm25. Está bien, pero que se note
  que el orden cambió, en vez de parecer que la lista se desordenó sola.
- **Los marcadores del fragmento están fijados en el store**: la llamada a
  `snippet()` usa corchetes como delimitadores y puntos suspensivos como elipsis.
  Si lo pintas en un `TextBlock` vas a ver los corchetes literales. Tienes dos
  salidas honestas: mostrarlo tal cual (simple, feo, cero riesgo) o resaltar de
  verdad con `RichTextBlock` y `Run`s —y entonces asumir que un transcript que
  contenga un corchete de verdad se va a ver raro—. Elige y escríbelo. Si cambias
  los marcadores, eso toca Infrastructure, y el autotest hoy sólo comprueba que
  el fragmento no viene vacío: **agrega ahí la comprobación que falte**.
- **El `limit` por defecto es 50.** Si el corpus crece, el usuario no va a saber
  que hay más. Muestra cuántos resultados hay, o di que están recortados.

---

## Lo que no puede pasar

**Un fallo de la base no puede tumbar la página ni la app.** Es la regla que
viene rigiendo la fase entera, y el precedente sigue siendo T4.4 — una excepción
de arranque se llevó puesta la app entera y costó nueve días encontrarla.
`RefreshAsync` y `LoadSessionDetailAsync` ya la cumplen: capturan, registran con
`App.LogStartupFailure` y dejan un mensaje. La búsqueda tiene que hacer lo mismo,
y con más razón, porque es la ruta que el usuario dispara más veces.

Pruébalo, no lo dejes en intención: apuntar la fábrica de conexiones a una ruta
imposible es como ya lo hacen dos de los autotests.

**Y una consulta del usuario nunca puede llegar a SQLite sin pasar por
`ToFts5Query`.** Está probado contra siete entradas hostiles; el modo de perder
esa garantía es construir la consulta en otro lado "para agregarle algo". Si
necesitas más poder de consulta, cámbialo **dentro** de `ToFts5Query` y extiende
el autotest.

---

## Cómo se construyen las páginas en este proyecto

No inventes un patrón nuevo — el paso 6 ya dejó `HistoryPage` y
`HistoryViewModel` como el ejemplo más cercano:

- Páginas con **constructor sin parámetros**, dependencias por
  `App.Services.GetRequiredService<T>()`.
- `MainWindow` navega **sin parámetro** y la página se reconstruye en cada
  navegación: la carga va en `Loaded`, y nada del estado sobrevive a salir y
  volver a entrar. Una búsqueda escrita tampoco — está bien, pero que no te
  sorprenda.
- MVVM con `CommunityToolkit.Mvvm`. `HistoryViewModel` ya está registrado en el
  contenedor.
- Las fechas se muestran con `SessionListItem.DisplayCulture` (`es-ES`, fija a
  propósito). No uses la cultura del sistema: ya produjo `"Thursday 27 de
  August"` en pantalla.
- Si agregas un tipo de item de lista, **dale `ToString()`**. Sin eso la
  automatización de interfaz lee `MeetingAssistant.App.ViewModels.<Tipo>` y un
  lector de pantalla anuncia eso. Ya pasó en este mismo archivo.
- La regla de arquitectura no se toca: `Core.csproj` tiene **0
  `PackageReference`** —compruébalo antes de dar el paso por terminado— y lo que
  necesites nuevo en Core que sea interfaz o modelo.

**Si algo no aparece en el árbol de UIA, mide `ActualWidth`/`ActualHeight` antes
de tocar el XAML.** El paso 6 perdió tres reestructuraciones persiguiendo un
defecto de layout que no existía: UIA simplemente no expone parte de esta página.

---

## Verificación — esto no se cierra leyendo el código

`AGENTS.md` lo pide y la fase lo viene cumpliendo: **build + corrida real**.

1. `dotnet build MeetingAssistant.sln` en **0 warnings, 0 errores**. El proyecto
   está en cero y se queda en cero.
2. Los cuatro autotests siguen en verde: `--verify-render`,
   `--verify-db-selftest` (29/29), `--verify-settings-config` (33/33),
   `--verify-reextraction` (18/18). **Si tocas el esquema, agrega una migración
   v2** — no edites el v1: hay bases con v1 aplicado, y editarlo hace que el
   archivo y el código dejen de coincidir sin que nada avise.
3. **Extiende `--verify-db-selftest`** con lo que hoy no está cubierto y que este
   paso hace posible romper. Es barato: corre sobre base temporal, sin red y sin
   proveedores. Como mínimo:
   - varias sesiones con transcripts distintos, y que la búsqueda devuelva **las
     que corresponden y en orden de `rank`**;
   - una sesión **sin** transcript que nunca aparece en resultados;
   - una consulta vacía o de puros espacios que devuelve lista vacía **sin
     consultar** (hoy `ToFts5Query` devuelve cadena vacía y el store corta antes;
     que quede fijado por un test);
   - lo que hayas decidido sobre los marcadores del fragmento.
4. **Usa `dotnet run --project src/MeetingAssistant.App` para probar en la app
   real.** `dotnet build` **no** refresca el layout `AppX\` que ejecuta el
   paquete registrado: lanzar por AUMID después de compilar corre el binario
   viejo sin ningún aviso. Ya costó una vuelta de depuración el 2026-08-28.
5. **En pantalla, con la base real.** Hoy tiene 6 sesiones, 1 transcript y 0
   reportes, así que con una sola reunión buscable la prueba es pobre. **Graba
   dos o tres reuniones antes**, con contenido distinto y reconocible.

   *Truco ya probado:* reproduce por el altavoz un `.wav` de
   `%LOCALAPPDATA%\MeetingAssistant\meeting-output\` mientras grabas — el
   loopback lo captura y Deepgram devuelve transcript de verdad. Una grabación en
   silencio muere en el guard de "transcripción vacía".

   Con eso, comprueba:
   - una palabra que sí se dijo aparece, con su fragmento, y **seleccionar el
     resultado abre el detalle correcto**;
   - una palabra que no se dijo da el mensaje de "sin resultados", distinto del
     de historial vacío;
   - una búsqueda con sesiones sin transcript en la base **dice que las hay**;
   - **re-extraer desde un resultado de búsqueda funciona** — es la prueba de que
     no rompiste la cadena `SelectedSession` → `ExtractForSessionAsync`;
   - borrar la búsqueda devuelve la lista completa;
   - escribir rápido y borrar no deja en pantalla resultados de una consulta
     vieja.
6. Provoca el fallo de base y confirma que la página avisa y la app sigue usable.

**Si una grabación falla con un error que parece de transcripción:** revisa el
consentimiento de micrófono. Desinstalar el paquete lo resetea a `Deny`, el error
aparece al **detener** y no al iniciar, y por el camino HTTP no deja ninguna
línea en el log. Escribir `Allow` en el registro **no alcanza** — hay que
activarlo desde *Configuración > Privacidad y seguridad > Micrófono*. Está
documentado en el paso 4 del roadmap.

---

## No lo arregles aquí

- **La vista de costo acumulado y la comparación entre prompts.**
  `GetCostSummaryAsync` y `GetPromptUsageAsync` ya existen y va a ser tentador.
  Es el **paso 8**, y tiene dos problemas de fondo que se ganaron su propio
  brief: léelo si te pica la curiosidad, pero no lo adelantes.
- **Los dos pendientes de GUI del usuario** — el botón "Generar reporte nuevo" y
  el Guardar de `SettingsPage`. Están en el roadmap, sección *Pendiente de
  confirmación manual*. Son suyos; no los des por cerrados desde acá.
- **Buscar dentro de los reportes**, no sólo de los transcripts. Sólo
  `transcript` está indexado, y es a propósito: el reporte se puede regenerar, el
  transcript no. Si te parece que hace falta, anótalo como backlog.
- **Editar el prompt desde Configuración.** El catálogo sigue siendo de sólo
  lectura, definido en código. Deuda de Fase 2.
- **El hueco de `LocalRecordingApiServer` → `IMeetingPipeline`** (no pasa por
  `RecordingCoordinator`, así que no actualiza la UI ni dispara toasts). Backlog
  de Fase 4.
- **La política de retención de `meeting-output\`.** Backlog.
- **Vaciar el `appsettings.json` empaquetado**, que sigue con credenciales en
  claro dentro del `.msix` de sólo lectura. Decisión aparte, anotada al cerrar el
  paso 5.

---

## Cierre

Actualiza el paso 7 de la sección de Fase 5 en
`roadmap-meeting-ai-assistant.md` con lo que quedó hecho y **lo que no se pudo
verificar, si algo queda así**. Marca también el criterio de salida "Puedes
buscar una palabra que se dijo en una reunión y encontrarla" si de verdad quedó
cumplido — y si lo cumpliste con un solo transcript en la base, dilo, porque no
es lo mismo.

En este proyecto el valor de los documentos es que se les pueda creer: un item
dado por bueno sin medirlo vale menos que uno marcado como no verificado.

Commitea en `main`. **Puedes hacer push**: el usuario trabaja con el repo
sincronizado. Antes de pushear, comprueba que no se cuela nada — el
`appsettings.json` real, `.pfx`/`.cer`, la ruta del vault o el nombre del
empleador. Hay un incidente previo por eso, documentado en `AGENTS.md`.
