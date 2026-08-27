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
2. Esquema y runner de migraciones (`PRAGMA user_version`), más la fábrica de
   conexiones. Archivo en `%LOCALAPPDATA%\MeetingAssistant\meetings.db`, mismo
   precedente que el log, el audio y la configuración.
3. Abstracciones en Core + implementación SQLite en Infrastructure.
4. El pipeline escribe sesión, transcript y reporte en la base **además** del
   `.md` del vault.
5. `SqliteConfigurationProvider` + DPAPI. Importar una sola vez el
   `appsettings.json` de usuario que creó T9 y marcarlo como migrado.
   `SettingsPage` no cambia de aspecto: sólo cambia dónde guarda.
6. `HistoryPage` de verdad: lista, detalle, y **re-generar un reporte desde un
   transcript viejo con otro prompt** — que hoy es imposible porque el
   transcript no se guarda.
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

**Las fechas se guardan en UTC.** Vale anotarlo porque hoy hay un desajuste
conocido: el `.wav` se nombra en hora local y el reporte en UTC, así que una
reunión de la noche aparece con fecha del día siguiente al ordenar por nombre.
La base es la oportunidad de arreglarlo — guardar UTC y convertir sólo al
mostrar.

### Riesgos, anotados antes de empezar

- ~~**El binario nativo.**~~ **Retirado el 2026-08-27**: medido contra el
  paquete instalado, carga y trae FTS5. Ver paso 1.
- **Una base corrupta no puede impedir arrancar la app.** Mismo tipo de fallo
  que un JSON truncado, y con peor precedente: en T4.4 una excepción de arranque
  se llevó puesta la app entera. Si la base no abre, hay que caer a la
  configuración empaquetada más entorno y avisar, no morir.
- **DPAPI es por usuario y por máquina.** Copiar el `.db` a otro perfil deja los
  secretos indescifrables. Es lo deseable, pero tiene que fallar con un mensaje
  claro, no con una excepción en el arranque.
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

- Puedes abrir Historial, ver tus reuniones pasadas, entrar a una y leer su
  reporte sin salir de la app.
- Puedes buscar una palabra que se dijo en una reunión y encontrarla.
- Puedes tomar un transcript viejo y volver a extraerlo con otro prompt.
- Puedes ver cuánto llevas gastado, real y acumulado.
- Las API keys ya no están en texto plano en ningún lado.

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
