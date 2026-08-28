# Roadmap — Asistente de IA para Reuniones de Asignación de Trabajo

**Tipo de proyecto:** Personal, independiente (no vinculado a CCAPAI)
**Stack:** .NET/C#, WinUI 3, Deepgram Nova-3 (transcripción), Gemini Flash-Lite (LLM extractor)
**Objetivo:** Transcribir reuniones de asignación de desarrollo → extraer summary, insights, tasklist e indicaciones mediante LLM → generar reporte estructurado.

**Nota de decisión (redefinición de plan):** se descartó Whisper (cualquier proveedor que lo hospede) y los modelos de Claude por prioridad costo/calidad. Transcripción vía Deepgram Nova-3 batch (~$0.0036/min, motor propio no-Whisper, tier gratis ~200 min/mes). LLM extractor vía Gemini Flash-Lite, elegido sobre DeepSeek V4 Flash por su desempeño multilingüe más sólido (relevante porque las reuniones mezclan español/inglés).

---

## Fase 0 — Spike técnico (validación de riesgos) ✅ COMPLETA

**Objetivo:** Confirmar que las piezas más inciertas técnicamente funcionan antes de invertir en arquitectura.

**Duración estimada:** 2-4 días
**Resultado:** 3 console apps aislados (audio, transcripción Deepgram, LLM agnóstico a proveedor) validados end-to-end con pruebas reales. Fix de audio documentado (polling/buffer). LLM spike soporta Gemini Flash-Lite y DeepSeek vía Azure Foundry con reporte de uso de tokens y costo real en ambos proveedores — listo para comparar costo/calidad entre ambos con transcripts reales. Sin bloqueos pendientes.

**Limitación conocida, diferida (no bloqueante):** transcripción de Deepgram falla en jerga técnica mixta español/inglés (ej. "stored procedure" → "auth restore procedure", "nulo" → "hulo"), y el LLM extractor propaga estos errores sin marcarlos como sospechosos — pueden aparecer como requirements/indications "oficiales" en el reporte. Mitigación identificada: Deepgram Keyterm Prompting (Nova-3 Multilingual, hasta ~100 palabras de glosario de dominio). Decisión: diferir el fix hasta después de validar el pipeline completo con reportes reales; revisar si el volumen de errores justifica implementarlo antes de confiar en los reportes sin revisión manual.

### Pasos
1. Scaffold de proyecto WinUI 3 vacío (template oficial, .NET 9)
2. Spike de captura de audio: loopback + micrófono simultáneo vía NAudio/WASAPI, guardado a `.wav`
3. Spike de transcripción: correr el `.wav` bueno (mic + loopback, ya validado) contra la API de Deepgram Nova-3 (batch), validar calidad de output en español/inglés mixto (dado que piensas en ambos idiomas) y confirmar diarización básica si la necesitas
4. Spike de llamada a Gemini API: request simple con Flash-Lite, confirmar autenticación, medir latencia real de un prompt de tamaño similar a una transcripción de reunión (15-30 min de audio ≈ 3000-6000 tokens de transcripción)

### Criterio de salida (Definition of Done)
- Tienes un `.wav` capturado, transcrito y un JSON de respuesta del LLM, generados manualmente en consola, sin ninguna arquitectura formal todavía.
- Si algún spike falla (ej. WASAPI loopback no captura correctamente audio de la app de meetings), lo sabes AQUÍ, antes de diseñar capas alrededor de un supuesto roto.

**Riesgo principal de esta fase:** captura de audio del sistema (loopback) en Windows tiene particularidades por dispositivo/driver. Si falla, el fallback es grabar solo micrófono + pedir que la reunión tenga speaker audible, lo cual es una degradación aceptable pero cambia el diseño de captura.

---

## Fase 1 — Core del pipeline (sin UI) ✅ COMPLETA

**Objetivo:** Pipeline funcional end-to-end ejecutable por consola/CLI. La lógica de negocio no debe depender de WinUI todavía.

**Duración estimada:** 1-2 semanas
**Resultado:** Estructura `Core`/`Infrastructure`/`Harness` migrada desde los spikes de Fase 0 (arquitectura validada de forma independiente: `Core` sin dependencias de proveedor, dirección de dependencias correcta). `MeetingReport` con parsing robusto, `ILlmReportExtractor` con prompt versionado (v1), `ICostEstimator` config-driven, `MarkdownReportStorage` y `MeetingPipeline` como orquestador real. Corrida end-to-end confirmada con ambos proveedores de LLM (Gemini Flash-Lite y DeepSeek vía Azure Foundry, swap por config) y reporte guardado como Markdown real en el vault de Obsidian. Un incidente de configuración resuelto en el camino (JSON inválido + vault path filtrado al archivo de ejemplo público — corregido, sin exposición real de credenciales confirmada por revisión de historial completo de git).

### Pasos
1. Definir el contrato del reporte (`Models`): `MeetingReport` con `Summary`, `Insights[]`, `TaskList[]`, `Requirements[]`, `OpenQuestions[]`, `Indications[]`
2. Diseñar el system prompt de extracción (versionado desde el día 1 — igual que haces con tu catálogo de clasificaciones, guarda versión del prompt junto con cada reporte generado, para poder comparar calidad entre iteraciones)
3. Construir `Infrastructure`:
   - `AudioCaptureService` (resultado del spike de Fase 0)
   - `DeepgramTranscriptionClient`
   - `GeminiReportExtractor`, implementando una interfaz `ILlmReportExtractor` (patrón ports-and-adapters — permite swap de proveedor por configuración si más adelante quieres comparar contra DeepSeek u otro modelo, sin tocar `Core`)
   - `LocalReportStorage` (export a Markdown, path configurable hacia tu vault de Obsidian)
4. Construir `Core`: orquestador que encadena captura → transcripción → prompt → parsing → guardado
5. Harness de consola: `dotnet run -- --file reunion.wav` que corre el pipeline completo

### Criterio de salida
- Puedes correr 3-5 reuniones reales (o grabaciones de prueba) end-to-end sin tocar UI
- El prompt ha pasado al menos 2 iteraciones basadas en output real (no solo en teoría)
- Tienes reportes reales guardados en tu vault para evaluar calidad

**Este es el momento de decidir si el prompt necesita ajuste antes de construir UI encima de un extractor mediocre.**

---

## Fase 2 — Shell WinUI 3 (MVP de interfaz)

**Objetivo:** Interfaz mínima usable para no depender de la consola.

**Duración estimada:** 1-2 semanas (incluye curva de aprendizaje de WinUI 3/MVVM)

### Pasos
1. Scaffold de proyecto con `CommunityToolkit.Mvvm`, estructura MVVM (`ViewModels`, `Views`, `Services`)
2. Shell de navegación (`NavigationView`) con 3 secciones: Grabar, Historial, Configuración
3. Vista "Grabar": botón iniciar/detener, indicador de estado, feedback de progreso (transcribiendo → analizando → listo)
4. Vista "Historial": lista de reportes generados (`ListView` virtualizado), click para abrir detalle
5. Vista "Detalle de reporte": renderizado de summary/insights/tasklist, botón de re-exportar a Obsidian
6. Vista "Configuración": API keys, path del vault, edición del system prompt

**Avance traído adelante (T8, 2026-08-14):** el prompt ya no es único ni
hardcodeado en el extractor. Hay un catálogo en Core — `assignment-meeting`,
`functional-spec` y, desde 2026-08-26, `feature-handoff` (handoff de una feature
a partir de la llamada con el tech lead: requisitos, alcance, riesgos, pasos y
criterios de aceptación). En RecordPage, después de la transcripción se elige el
prompt, se ve el texto, se genera el reporte y se muestra el Markdown.
Tray/HTTP siguen auto-extrayendo con el prompt por defecto.

**Pasos 4 y 5 cerrados desde Fase 5 (2026-08-28).** `HistoryPage` con lista y
detalle existe, leyendo de la base en vez de escanear un directorio — que es por
lo que se absorbieron a Fase 5 en vez de construirlos dos veces. Lo único que
sigue abierto de esta fase es **editar el system prompt desde Configuración**
(paso 6): el catálogo sigue siendo de sólo lectura, definido en código.

### Criterio de salida
- Puedes grabar una reunión completa desde la UI, sin tocar consola, y ver el reporte generado en pantalla

---

## Fase 3 — Integración al flujo de trabajo diario ✅ COMPLETA

**Objetivo:** Que la herramienta desaparezca en tu flujo, no que sea una app que abres manualmente.

**Duración estimada:** 3-5 días
**Resultado (cerrada 2026-08-26):** tray icon con menú contextual, hotkey global
`Ctrl+Alt+F9`, endpoint HTTP local autenticado por token, toasts para todo el
ciclo, autostart opt-in y MSIX firmado instalado de forma persistente. El pase
de aceptación final (T6b) se corrió **contra el paquete instalado, no con
`dotnet run`**: identidad de paquete, log de arranque, endpoint, bandeja,
hotkey, toasts, autostart y desinstalación limpia. El pipeline completo corrió
end-to-end bajo la instalación y guardó reporte en el vault. El detalle
item-por-item, con lo que quedó sin verificar y por qué, está en `TASK_GRAPH.md`.

**Deuda conocida que queda de esta fase, al backlog de Fase 4:** una grabación
disparada por HTTP no pasa por `RecordingCoordinator`, así que no actualiza
`RecordPage` ni dispara toasts, y sus fallos no quedan en el log — sólo viajan
en el cuerpo de la respuesta. Además `meeting-output\` crece sin política de
retención, y la desinstalación **no** borra `%LOCALAPPDATA%\MeetingAssistant\`
(decisión explícita, no descuido).

### Pasos
1. Tray icon con menú contextual (iniciar/detener grabación sin abrir la ventana principal)
2. Hotkey global para iniciar/detener grabación
3. Endpoint HTTP local (`HttpListener`, sin Kestrel) para disparar grabación externamente: `POST /recording/start`, `POST /recording/stop` (síncrono, responde con transcript + reporte + ruta guardada). Requiere token de autenticación por header — no negociable, dado que enciende el micrófono remotamente. Solo bind a `localhost`, nunca a `0.0.0.0`.
4. Notificación (Toast/`InfoBar`) cuando el reporte esté listo
5. Autostart opcional en boot de Windows — DONE 2026-08-25 (opt-in `StartupTask`, validado en GUI y con el MSIX firmado reinstalado)
6. Empaquetado MSIX para instalación local persistente — DONE 2026-08-25/26 (identidad de paquete real + certificado autofirmado, `.msix` x64 firmado e instalado; desinstalación limpia verificada, incluido el paso manual de sacar el certificado de `LocalMachine\TrustedPeople`, que Windows no revierte solo)

### Criterio de salida — ✅ CUMPLIDO 2026-08-26
- Puedes iniciar una grabación con un hotkey sin interrumpir tu flujo de trabajo actual, y recibir una notificación cuando el reporte está en tu vault

---

## Fase 4 — Calidad e iteración continua

**Objetivo:** Esta fase no tiene fecha de cierre — es mantenimiento activo del sistema.

### Actividades recurrentes
- Comparar calidad de reportes entre versiones de prompt (mantén un log de versiones, como haces con tus RCA)
- Ajustar el prompt cuando detectes que el LLM omite tipos de información específicos de tus reuniones (patrones recurrentes de tu equipo/dominio)
- Evaluar si conviene mover ciertos pasos a batch processing si empiezas a procesar varias reuniones de una sola vez (revisar si Gemini o Deepgram ofrecen descuento por batch en el momento en que llegues aquí — confirma precios vigentes, no asumas los de hoy)
- Revisar costo real acumulado vs. estimado

---

## Fase 5 — Persistencia local, historial y búsqueda

**Decidida el 2026-08-27.** Es un cambio arquitectónico, no pulido de UI.

**Objetivo:** que las reuniones dejen de vivir sólo como archivos sueltos. Hoy
el transcript es **efímero** — existe durante el pipeline y se pierde — y lo
único que sobrevive es el `.md` en el vault. Eso impide tres cosas que el
proyecto ya quiere: buscar por contenido, comparar calidad entre versiones de
prompt, y ver el costo acumulado de verdad.

**Absorbe los pasos 4 y 5 de Fase 2** (Historial y Detalle de reporte). Se
decidió así en vez de construir `HistoryPage` sobre un escaneo de directorio y
migrarla después: sería escribirla dos veces.

### Por qué ahora, y por qué no antes

No es una idea nueva que aparece de la nada — es lo que destrabó cerrar Fase 3:

- `IReportStorage` **sólo sabe guardar** (`SaveAsync` / `SaveMarkdownAsync`). No
  hay lado de lectura, así que `HistoryPage` no tiene de dónde leer. Ese es el
  trabajo real detrás de la página, no el XAML.
- Cada reporte ya lleva `prompt-id`, `prompt-version`, `tokens-input/output` y
  `cost-usd` en el frontmatter. Los datos de Fase 4 **ya se están generando** y
  nadie los consulta, porque están desparramados en Markdown.
- La limitación diferida de Fase 0 — Deepgram destroza jerga técnica mezclada
  ES/EN — sigue sin decidirse por falta de evidencia. Con transcripts guardados
  y buscables, medir la frecuencia real de esos errores pasa a ser una consulta,
  no una impresión.

### Decisiones tomadas (2026-08-27)

| Decisión | Elegido | Nota |
|---|---|---|
| Motor | **SQLite** con `Microsoft.Data.Sqlite`, ADO.NET a mano | Sin EF Core: no hay ORM en este código y las migraciones no compensan el riesgo de trimming |
| Alcance | **Todo**: sesiones, transcripts, reportes, configuración y API keys | |
| Transcripts | **Se guardan indefinidamente** | Habilita búsqueda y re-extracción con otro prompt |
| Encaje | Fase nueva que **absorbe** el historial de Fase 2 | |

**LiteDB fue el segundo**, y gana justo en el riesgo principal (no tiene binario
nativo). Si el `.dll` de SQLite resulta problemático dentro del MSIX, se cambia:
las interfaces de Core no cambiarían.

### Reglas de diseño, para que no se pierdan

1. **El vault sigue siendo el producto.** Obsidian es donde realmente se leen
   los reportes. La base es sistema de registro e índice; el `.md` del vault
   **sigue escribiéndose igual**. `MarkdownReportStorage` no se reemplaza, se
   compone.
2. **No normalizar la estructura del reporte.** El catálogo produce formas
   distintas a propósito: `assignment-meeting` da un `MeetingReport`
   estructurado, `functional-spec` y `feature-handoff` dan Markdown suelto.
   Tablas para `TaskItem`/`Insights` pelearían con ese diseño. Se guarda el
   Markdown más una columna `structured_json` opcional.
3. **`Core` sigue sin referencias a proveedores.** Las interfaces
   (`IMeetingHistoryStore`, `ISettingsStore`) van en Core; SQLite vive en
   Infrastructure. La regla de `AGENTS.md` no se toca.
4. **La configuración en base se implementa como `IConfigurationProvider`, no
   reemplazando `IConfiguration`.** Es lo que deja intactos
   `ReadRequiredSetting`, `StartupConfigurationValidator` y
   `ConfigPricingCostEstimator` — y, sobre todo, **conserva las variables de
   entorno `Seccion__Clave` como capa de arriba**. Esa vía de escape ya salvó
   una validación (se usó para forzar el fallo de Deepgram) y es lo que queda
   cuando un ajuste malo impide arrancar. Orden: empaquetado → SQLite → entorno.
5. **Las API keys se cifran con DPAPI** (`ProtectedData`, ámbito
   `CurrentUser`), en las filas marcadas como secreto. **Sin esto, mover las
   claves a la base no mejora nada**: SQLite no cifra, así que sería pasarlas de
   un archivo en claro a otro. Con esto, es la primera vez que las credenciales
   dejan de estar en texto plano.

### Pasos

1. ~~**Spike: probar el binario nativo de SQLite dentro del MSIX instalado**~~
   **✅ HECHO 2026-08-27 — pasa.** Se agregó `Microsoft.Data.Sqlite` 10.0.11 a
   Infrastructure y un `SqliteEnvironmentProbe` que corre en cada arranque. Bajo
   el paquete **instalado y firmado**, corriendo desde
   `C:\Program Files\WindowsApps`: `e_sqlite3.dll` (1,98 MB, x64) **viajó dentro
   del paquete**, SQLite **3.53.3** cargó, y **FTS5 está disponible** —
   verificado creando una tabla virtual, no leyendo flags de compilación. La
   decisión de SQLite sobre LiteDB queda confirmada contra la máquina, no
   supuesta, y **el riesgo número uno de la fase está retirado**. Falta el mismo
   chequeo si alguna vez se empaqueta en Release, donde el trimming sigue sin
   ejercerse; el probe queda permanente justamente para eso.
2. ~~Esquema y runner de migraciones~~ **✅ HECHO 2026-08-27.**
   `SqliteConnectionFactory` (PRAGMA por conexión: WAL, `foreign_keys`,
   `busy_timeout`) y `SqliteSchemaMigrator` sobre `PRAGMA user_version` — cada
   paso en su propia transacción junto con su bump de versión, así que un fallo
   a mitad deja la base en la versión anterior, entera. Esquema v1 con `session`,
   `transcript`, `report`, `setting`, la tabla FTS5 y sus tres triggers.
   Migración corrida **bajo el paquete instalado**: `v0 -> v1`, base creada en
   `%LOCALAPPDATA%\MeetingAssistant\meetings.db`. Dos comandos nuevos en el
   harness, en la línea de `--verify-render`: `--verify-db` inspecciona la base
   real y `--verify-db-selftest` prueba el esquema sobre una base temporal.
   **12 de 12 comprobaciones en verde**, incluidas las que importan y se rompen
   en silencio: los triggers de insert/update/delete manteniendo el índice, el
   borrado en cascada, y la búsqueda sin acentos encontrando texto acentuado
   (`sesion` → `sesión`), que es la razón de elegir el tokenizador
   `unicode61 remove_diacritics 2` para un corpus ES/EN.
3. ~~Abstracciones en Core + implementación SQLite en Infrastructure.~~
   **✅ HECHO 2026-08-27.** En Core: `IMeetingHistoryStore` — el **lado de
   lectura que nunca existió**, y que es el trabajo real detrás de
   `HistoryPage`—, más `ISettingsStore` e `ISecretProtector`, con sus modelos.
   En Infrastructure: `SqliteMeetingHistoryStore`, `SqliteSettingsStore` y
   `DpapiSecretProtector`. `Core.csproj` sigue con **0 referencias a paquetes**.
   **29 de 29 comprobaciones en verde** en `--verify-db-selftest`.

   **DPAPI se adelantó del paso 5 a propósito:** un almacén de ajustes que
   escribe secretos en claro estaría mal desde el primer día, y dejar un
   "protector" que no protege como relleno temporal es la clase de cosa que
   nadie vuelve a mirar. El autotest lo comprueba de la única forma que vale:
   **lee la fila directamente del archivo y verifica que el texto en claro no
   está ahí**.
4. ~~El pipeline escribe sesión, transcript y reporte en la base~~
   **✅ HECHO 2026-08-27**, con una salvedad medida que se detalla abajo.

   **Punto de enganche elegido: `MeetingPipeline`, no `RecordingCoordinator`.**
   Es el único punto por el que pasan **todos** los caminos.
   `LocalRecordingApiServer` llama a `IMeetingPipeline` directo, sin pasar por el
   coordinador, así que engancharlo allá habría dejado sin registrar justo las
   grabaciones por HTTP — las que más se usan sin ventana. Un solo punto, sin
   filas duplicadas. `MeetingPipeline` vive en Core pero sólo compone
   abstracciones, e `IMeetingHistoryStore` es una de ellas: la regla de
   arquitectura se mantiene y `Core.csproj` sigue con 0 referencias a paquetes.

   `StartRecordingAsync` pasó a exigir un `source` **sin valor por defecto**: es
   un dato que sólo conoce quien llama, y con un default cada llamador nuevo
   heredaría en silencio una etiqueta equivocada, que es como esa columna dejaría
   de servir para lo único que existe. El compilador obliga a cada call site a
   declararse.

   El historial es una dependencia **opcional** del pipeline: el harness corre el
   pipeline de verdad y no debe ensuciar el historial del usuario con corridas de
   prueba. Y el log de fallos entra como delegado, porque Core no puede
   referenciar `App`.

   **Verificado:**
   - Build en 0 warnings / 0 errores; `--verify-db-selftest` sigue en 29/29.
   - **`--verify-pipeline-history <wav>`, nuevo en el harness: 15/15.** Corre el
     pipeline completo contra una base temporal y comprueba que quedan sesión,
     transcript y reporte, que el reporte apunta al `.md` del vault, que el
     markdown guardado coincide con el generado, y que `structured_json` sólo
     aparece con `assignment-meeting`.
   - **La resiliencia se probó de verdad, no se dio por hecha**: con la base
     apuntada a una ruta imposible, la grabación **igual llegó al vault** con su
     transcript, y los fallos de base se registraron en vez de propagarse.
   - **Contra el paquete instalado**: tres grabaciones dejaron sus filas de
     sesión con el `source` correcto y **distinto según el camino** (`hotkey`,
     `hotkey`, `http`). El harness gana `--verify-db` con listado de sesiones
     para poder verlo.

   **Un defecto real que encontró el test y que la lectura de código no vio:**
   el reporte se registraba **sólo** en `ExtractAndSaveAsync` (el flujo de dos
   pasos de la ventana) y no en `ExtractSessionAsync`, que es por donde pasan
   hotkey, bandeja, HTTP e importación. O sea: los caminos más usados guardaban
   sesión y transcript pero **ningún reporte**. Corregido y re-verificado.

   **Lo que NO se verificó, dicho explícitamente:** el camino de éxito completo
   (filas de transcript y reporte) **contra el MSIX instalado**. Lo bloqueó un
   problema de plataforma, no del código — ver abajo. Lo que sí quedó medido
   contra la instalación es que las sesiones se crean y se etiquetan bien.

   > **Hallazgo de plataforma: desinstalar el paquete resetea el consentimiento
   > de micrófono de Windows a `Deny`.** Después del ciclo de
   > desinstalar-e-instalar, `WasapiCapture.InitializeCaptureDevice` empezó a
   > devolver `E_ACCESSDENIED` y **toda grabación falla**. Es especialmente
   > traicionero por tres razones: el error aparece al **detener**, no al
   > iniciar (el toast de `RecordingStarted` sale igual, ver T6b paso 0); parece
   > un fallo de transcripción; y por el camino HTTP **no deja ninguna línea en
   > el log**, sólo viaja en el cuerpo de la respuesta.
   >
   > Comprobado que **no** es el dispositivo ocupado: el harness, que corre sin
   > empaquetar, graba sin problema con el mismo código de captura. Es el
   > consentimiento por paquete:
   > `HKCU:\...\CapabilityAccessManager\ConsentStore\microphone\{PFN}` estaba en
   > `Deny`.
   >
   > **Escribir `Allow` en el registro directamente NO alcanza** — se probó, y la
   > app siguió recibiendo `E_ACCESSDENIED` tras reiniciarla. Windows cachea el
   > consentimiento de apps empaquetadas, así que hay que activarlo desde
   > *Configuración > Privacidad y seguridad > Micrófono*. Queda anotado porque
   > es exactamente la clase de fallo que este proyecto ya sufrió dos veces:
   > funciona, se reinstala, y deja de funcionar sin que nada obvio lo explique.
5. ~~`SqliteConfigurationProvider` + DPAPI. Importar una sola vez el
   `appsettings.json` de usuario que creó T9 y marcarlo como migrado.
   `SettingsPage` no cambia de aspecto: sólo cambia dónde guarda.~~
   **✅ HECHO 2026-08-28.**

   `SqliteConfigurationSource`/`SqliteConfigurationProvider` en Infrastructure,
   `UserSettingsImporter` para la migración de una sola vez, y
   `SettingKeyPolicy` en Core — que es quien decide qué se cifra. Ese detalle no
   es organizativo: la UI, el importador y el comando del harness escriben
   secretos por caminos distintos, y **si discreparan la credencial entraría en
   claro por uno de ellos sin que nada se rompa**. Una sola respuesta, en Core.

   **La pila quedó, de menor a mayor precedencia:** empaquetado → archivo de
   usuario (legado de T9) → **SQLite** → entorno. El archivo de T9 se conserva
   como capa aunque el importador se lo lleve en el primer arranque, y esa no es
   una capa de más: es el caso en que **la base no abrió** — el import no
   ocurrió, el archivo sigue ahí, y la app arranca con la configuración del
   usuario igual que antes. Sin esa capa, una base rota dejaría a la app sin
   vault y sin claves.

   `ReadRequiredSetting`, `StartupConfigurationValidator` y
   `ConfigPricingCostEstimator` **no se tocaron**: ninguno se enteró de que la
   base existe. Es lo que compra apilarse dentro de `IConfiguration` en vez de
   reemplazarlo.

   **El importador no borra el archivo del usuario: lo reemplaza por una copia
   redactada** (`appsettings.pre-sqlite.json`), y sólo después de **releer cada
   clave y comprobar que coincide** — o sea, después de verificar que el ciclo
   completo de cifrado y descifrado funciona en ESE perfil, no que la escritura
   no dio error. Si algo falla, el original queda intacto y la app sigue leyendo
   de él. Se importa **todo** el archivo, no las nueve claves que sabe editar
   `SettingsPage`: `Hotkey` y `Api` quedaron fuera de la UI a propósito y una
   lista blanca las habría dejado atrás en silencio.

   **Verificado:**
   - Build en 0 warnings / 0 errores; los autotests anteriores siguen en verde
     (`--verify-db-selftest` 29/29, `--verify-render` OK).
   - **`--verify-settings-config`, nuevo en el harness: 33/33.** Precedencia
     entre las cuatro capas, secretos legibles a través de `IConfiguration` y
     cifrados en disco, un secreto indescifrable que **no lanza** y deja ver la
     capa de abajo, una base en ruta imposible que no impide construir la
     configuración, `Reload()`, y el importador entero: anidamiento de tres
     niveles con `:` en el nombre de la propiedad, marcadores omitidos,
     idempotencia, y **un import contra una base rota que deja el archivo del
     usuario intacto**.
   - **Contra la app real, con el archivo real del usuario:** las 8 claves
     migraron, las 3 API keys quedaron cifradas y **descifrables en este perfil**
     (40/53/84 caracteres, idénticos al origen), el `appsettings.json` ya no está
     en la ruta que lee `IConfiguration`, y el segundo arranque reportó "ya
     migrados" sin volver a tocar nada.
   - **La precedencia se midió en la app real, no se dedujo del código.** Con
     `Api:Port` — el empaquetado dice 5757 — la app escuchó en **5758** con la
     fila en la base, y en **5759** con la variable de entorno puesta encima. Las
     dos sondas se deshicieron y volvió a 5757. Vale haberlo medido: el
     empaquetado tiene valores reales para las mismas claves, así que *la app
     arrancando bien no probaba nada* — la capa nueva podría no haber estado
     haciendo nada.

   **Dos hallazgos de plataforma, anotados porque cuestan tiempo:**

   > **`dotnet build` NO refresca el layout `AppX\` que ejecuta el paquete
   > registrado.** El registro de desarrollo apunta a
   > `bin\x64\Debug\...\win-x64\AppX`, y `dotnet build` deja los ensamblados
   > nuevos un directorio más arriba (`win-x64\`). Lanzar por AUMID después de
   > compilar **corre el binario viejo** sin ningún aviso: el primer intento de
   > verificar esto pareció "el import no se ejecuta" cuando en realidad era
   > código de dos horas antes. Para probar cambios hay que usar
   > `dotnet run --project src/MeetingAssistant.App`, que sí re-registra el
   > layout.
   >
   > **Una app empaquetada no hereda las variables de entorno de la consola.** Se
   > activa por el broker del shell, no como hijo del proceso que la lanza:
   > `Api__Port=5759 dotnet run ...` no tuvo ningún efecto. La vía de escape
   > funciona con la variable a **nivel de usuario**
   > (`[Environment]::SetEnvironmentVariable('Api__Port','5759','User')`), que es
   > como se usó la vez que salvó una validación. Importa dejarlo escrito: la
   > regla de diseño 4 apuesta a esa vía de escape para el caso en que un ajuste
   > malo impida arrancar, y el modo obvio de invocarla no sirve.

   **Lo que NO se verificó, dicho explícitamente:** el botón Guardar de
   `SettingsPage` no se pulsó — requiere GUI y no hay forma de ejercitarlo desde
   el harness, porque `UserSettingsService` vive en el proyecto `App`. Lo que sí
   está comprobado es que las claves que escribe `SaveAsync` son exactamente las
   que lee `LoadEffective`, y que el `ISettingsStore.SetAsync` que usa por debajo
   —con el mismo `SettingKeyPolicy`— cifra y borra como debe. Queda como el
   primer chequeo de GUI del paso 6.

   **Y una consecuencia que el criterio de salida de la fase no cubre:** el
   `appsettings.json` **empaquetado** sigue teniendo credenciales en claro
   (dentro del `.msix`, de sólo lectura, documentado en T6a). Este paso saca las
   claves del archivo de usuario, que es el editable; el empaquetado es la capa
   de fábrica y vaciarlo es una decisión aparte.
6. ~~`HistoryPage` de verdad: lista, detalle, y **re-generar un reporte desde un
   transcript viejo con otro prompt** — que hoy es imposible porque el
   transcript no se guarda.~~ **✅ HECHO 2026-08-28**, con un pendiente de GUI
   que se detalla abajo.

   `HistoryViewModel` + `HistoryPage` como maestro-detalle en una sola página
   (el `NavigationView` tiene el botón de retroceso colapsado y el `Frame` navega
   sin parámetro, así que una página de detalle aparte habría costado inventar
   las dos cosas). El stub de 11 líneas que decía "Próximamente" ya no existe.

   **El defecto que encontró este paso, y que la lectura de código no había
   visto:** `MeetingPipeline` guarda la sesión en curso en `_currentSessionId`,
   un campo de instancia, y el pipeline **está registrado como singleton**.
   `ExtractAndSaveAsync` deduce de ese campo a qué sesión pertenece el reporte.
   Re-extraer desde el historial por ese camino habría hecho una de dos cosas,
   las dos silenciosas: colgar el reporte de la **última grabación** en vez de la
   reunión re-extraída, o —si no hubo grabación en esa sesión de la app— abrir
   una **sesión fantasma** marcada como importación, duplicando una reunión que
   ya existía. En ambos casos la base queda consistente, el `.md` llega al vault,
   y el historial miente.

   Corregido con `ExtractForSessionAsync(sessionId, transcript, promptId)` en
   `IMeetingPipeline`: la sesión entra **por parámetro** y ese camino no lee ni
   escribe `_currentSessionId`. Eso además lo hace seguro frente a una grabación
   simultánea por HTTP, que sí puede ocurrir porque el endpoint llama al pipeline
   directo. `ExtractAndSaveAsync` se quedó igual: sigue siendo el flujo de dos
   pasos de la ventana y el de "Adjuntar transcripción (.txt)". La re-extracción
   pasa por `RecordingCoordinator` para heredar su `_operationLock` —lo que de
   verdad serializa— y para disparar el toast de reporte guardado.

   **Verificado:**
   - Build en 0 warnings / 0 errores; los tres autotests anteriores siguen en
     verde (`--verify-render`, `--verify-db-selftest` 29/29,
     `--verify-settings-config` 33/33).
   - **`--verify-reextraction`, nuevo en el harness: 18/18.** Corre con dobles de
     prueba —sin Deepgram, sin LLM y sin micrófono—, así que es gratis y
     determinístico: lo que se comprueba es a qué fila apunta una foreign key, y
     pagarle a dos proveedores para eso sería absurdo. Además el micrófono puede
     estar bloqueado por el consentimiento de Windows, que ya bloqueó una
     verificación del paso 4.
   - **El test se comprobó contra el defecto**, que es lo que le da valor: con
     `ExtractForSessionAsync` delegando al camino viejo, **6 comprobaciones se
     ponen en rojo**, incluidas las dos decisivas ("el reporte re-extraído quedó
     en la reunión VIEJA" y "y NO se coló en la grabación de hoy"). Un test que
     pasa igual con y sin la corrección no habría probado nada.
   - **En pantalla, contra la app real y la base real** (6 sesiones, 1 transcript,
     0 reportes): la lista trae las 6 reuniones con hora local y su origen
     traducido; las 5 sesiones sin transcript se muestran como tales y no
     revientan al abrirlas; el detalle de la única con transcript lo carga desde
     la base (9.541 caracteres, Deepgram, 27/08/2026 16:00); y "Mostrar el .md en
     el Explorador" aparece correctamente apagado, porque esa sesión no tiene
     reportes.

   **Dos defectos más, encontrados inspeccionando la app corriendo:**

   - **Fechas mitad en inglés.** El título salía `"Thursday 27 de August"` — los
     nombres de día y mes en la cultura del sistema (en-US) alrededor de un "de"
     español. Toda la UI está escrita en español y no hay localización, así que
     las fechas que se muestran usan una cultura fija (`es-ES`).
   - **Nombre de accesibilidad inservible.** Cada fila se anunciaba como
     `"MeetingAssistant.App.ViewModels.SessionListItem"`: WinUI usa el
     `ToString()` del objeto cuando la plantilla no expone un nombre. Un lector
     de pantalla leía eso. Resuelto con `ToString()` en los dos items de lista.

   **Una vuelta perdida que vale anotar, para no repetirla.** La automatización de
   interfaz (UIA) **no expone** la sección de re-extracción de esta página: ni el
   `ComboBox` ni el botón aparecen en el árbol. Eso se leyó como "no se está
   construyendo" y costó **tres reestructuraciones del XAML** persiguiendo un
   defecto de layout que no existía. Medido desde dentro de la app, con una
   sesión seleccionada, el botón mide **220x32 y es visible** y el panel 663 de
   alto. La regla que queda: si algo no aparece en UIA, **medir
   `ActualWidth`/`ActualHeight` antes de tocar el XAML**. (La estructura final
   —lo que se lee con scroll arriba, la acción fija abajo— se conserva porque está
   medida y es mejor UX, no porque arreglara nada.)

   **Lo que NO se verificó, dicho explícitamente:** el botón **"Generar reporte
   nuevo" no se pulsó**. Es una llamada real al LLM que cobra y escribe un `.md`
   en el vault del usuario, así que quedó para que la dispare él. Todo lo que
   está debajo del botón sí está probado end-to-end en `--verify-reextraction`:
   el comando del coordinador, el `sessionId` explícito, la fila nueva de reporte
   en la sesión correcta y el `.md` en el vault sin pisar el anterior. Sigue
   pendiente, de paso, el **botón Guardar de `SettingsPage`** del paso 5, por la
   misma razón (requiere GUI).
7. Búsqueda full-text sobre transcripts (FTS5).
8. Vista de costo acumulado y comparación entre versiones de prompt. Es el
   primer entregable que le paga a Fase 4.

### Esquema, en borrador

Una sesión tiene un transcript y **puede tener varios reportes**: el catálogo ya
permite re-correr el mismo transcript con otro prompt, y eso hoy se pierde.

```
session   id, started_at_utc, ended_at_utc, audio_path, duration_seconds, source
            -- source: hotkey | tray | http | archivo importado

transcript  session_id, text, provider, model, cost_usd, created_at_utc
transcript_fts  -- tabla virtual FTS5 sobre transcript.text

report    id, session_id, prompt_id, prompt_version, markdown,
          structured_json,            -- sólo assignment-meeting; null en el resto
          llm_provider, llm_model, tokens_input, tokens_output, cost_usd,
          vault_path,                 -- dónde quedó el .md exportado
          created_at_utc

setting   key, value, is_secret, updated_at_utc
            -- key con la misma forma que IConfiguration: "Storage:VaultPath"
            -- value cifrado con DPAPI cuando is_secret = 1
```

Dos detalles que no son accidentales: `structured_json` es una columna y no un
juego de tablas (regla de diseño 2), y `vault_path` deja el `.md` del vault como
lo que es — una exportación, no el registro (regla 1).

**El costo va en micro-dólares enteros** (`cost_micro_usd`), no en `REAL`.
Guardar dinero en punto flotante es pedir deriva justo en la métrica que Fase 4
quiere **sumar**. La granularidad de 1e-6 no pierde nada: es exactamente la que
ya muestra el frontmatter, que renderiza `cost-usd` con F6.

**Las fechas se guardan en UTC.** Vale anotarlo porque hoy hay un desajuste
conocido: el `.wav` se nombra en hora local y el reporte en UTC, así que una
reunión de la noche aparece con fecha del día siguiente al ordenar por nombre.
La base es la oportunidad de arreglarlo — guardar UTC y convertir sólo al
mostrar.

### Riesgos, anotados antes de empezar

- ~~**El binario nativo.**~~ **Retirado el 2026-08-27**: medido contra el
  paquete instalado, carga y trae FTS5. Ver paso 1.
- ~~**Una base corrupta no puede impedir arrancar la app.**~~ **Cubierto en el
  paso 5, y medido.** Era el riesgo que más creció ahí: hasta el paso 4 una base
  rota sólo costaba el historial; desde el paso 5 está en el camino crítico de la
  configuración. `SqliteConfigurationProvider.Load()` **nunca lanza** — si falla,
  la capa queda vacía, el fallo se registra y la app cae a empaquetado + archivo
  de usuario + entorno. Comprobado en el autotest con la base apuntada a una ruta
  imposible: la configuración se construye igual y el empaquetado sigue visible.
- ~~**DPAPI es por usuario y por máquina.**~~ **Cubierto en el paso 5.** Un
  secreto que no se puede descifrar vuelve como `null` y la clave simplemente no
  se publica en la capa: se ve la de abajo y, si tampoco la tiene,
  `StartupConfigurationValidator` la reporta como faltante con un mensaje que se
  entiende. Nunca una excepción de criptografía en el arranque. `--verify-db`
  ahora dice de cada secreto si es **descifrable en este perfil**, que es lo
  único que hay que poder ver de un valor cifrado.
- **Contenido de reuniones en reposo, en claro, para siempre.** Es la
  consecuencia asumida de guardar transcripts indefinidamente. Los secretos se
  cifran; **los transcripts no**, porque cifrarlos mata la búsqueda FTS5. Queda
  dicho explícitamente: es un intercambio elegido, no un descuido.
- **La desinstalación no borra nada de esto.** La decisión de conservar
  `%LOCALAPPDATA%\MeetingAssistant\` se tomó cuando ahí había un log y unos
  `.wav`. Ahora pasaría a haber **todos los transcripts**. Sigue vigente, pero
  vale re-mirarla cuando la base exista.
- **El `.db` pasa a ser lo más valioso del sistema** y no hay respaldo de nada.
  Un archivo único es fácil de copiar; que sea fácil no significa que alguien lo
  esté haciendo.

### Criterio de salida

- ~~Puedes abrir Historial, ver tus reuniones pasadas, entrar a una y leer su
  reporte sin salir de la app.~~ **Cumplido (paso 6, 2026-08-28)**, con la
  salvedad de que la base real todavía no tiene ningún reporte guardado: lo que
  se vio en pantalla fue la lista, el detalle y el transcript. El renderizado de
  un reporte se ejercita en cuanto exista uno.
- Puedes buscar una palabra que se dijo en una reunión y encontrarla.
- ~~Puedes tomar un transcript viejo y volver a extraerlo con otro prompt.~~
  **Construido y probado en el paso 6**, con 18/18 en `--verify-reextraction`,
  incluida la atribucion de sesion. Falta el clic real: es una llamada al LLM
  que cobra y escribe en el vault, y se dejo para el usuario.
- Puedes ver cuánto llevas gastado, real y acumulado.
- ~~Las API keys ya no están en texto plano en ningún lado.~~ **Cumplido para el
  archivo editable (paso 5, 2026-08-28):** las tres claves del usuario están
  cifradas con DPAPI en `meetings.db` y el `appsettings.json` de su perfil ya no
  existe. Queda **una salvedad, no un pendiente del paso**: el `appsettings.json`
  *empaquetado* sigue con credenciales en claro dentro del `.msix` de sólo
  lectura — es la capa de fábrica, y vaciarla es una decisión aparte.

---

## Resumen de dependencias entre fases

```
Fase 0 (spike) ──> Fase 1 (core pipeline) ──> Fase 2 (UI) ──> Fase 3 (integración) ──> Fase 4 (iteración continua)
                                                  │
                                                  └──> Fase 5 (persistencia local) ──> alimenta Fase 4
```

Fase 5 se cuelga de Fase 2 porque absorbe sus pasos 4 y 5 (Historial y Detalle),
y alimenta a Fase 4: la comparación entre versiones de prompt y el costo
acumulado dejan de ser trabajo manual cuando hay dónde consultarlos.

No hay atajos razonables: construir UI (Fase 2) antes de validar que el extractor produce reportes útiles (Fase 1) es el error más común en este tipo de proyecto — terminas puliendo una interfaz para una lógica de negocio que todavía no confías.
