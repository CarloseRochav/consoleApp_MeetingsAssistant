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

## Resumen de dependencias entre fases

```
Fase 0 (spike) ──> Fase 1 (core pipeline) ──> Fase 2 (UI) ──> Fase 3 (integración) ──> Fase 4 (iteración continua)
```

No hay atajos razonables: construir UI (Fase 2) antes de validar que el extractor produce reportes útiles (Fase 1) es el error más común en este tipo de proyecto — terminas puliendo una interfaz para una lógica de negocio que todavía no confías.
