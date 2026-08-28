# Brief para Fase 5, paso 8 — Costo acumulado y comparación entre prompts

Generado el 2026-08-28, después del commit `223382f` (paso 6 cerrado). El plan de
la fase vive en `roadmap-meeting-ai-assistant.md`, sección
`## Fase 5 — Persistencia local, historial y búsqueda`; este archivo es la orden
de trabajo.

**Este paso cierra Fase 5.** Es también el primer entregable que le paga a Fase
4: la comparación entre versiones de prompt y el costo acumulado dejan de ser
trabajo manual cuando hay dónde consultarlos.

**Depende del paso 7** sólo por orden de trabajo, no técnicamente. Si el 7 ya
está hecho, la lista y la búsqueda son el vecindario donde esto va a vivir.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir código lee `AGENTS.md` completo y la sección de Fase 5 del
roadmap — en particular **las cinco reglas de diseño**. Y presta atención
especial a una regla de `AGENTS.md` que en este paso deja de ser genérica y pasa
a ser el centro del trabajo:

> **Nunca inventes valores: precios, credenciales, resultados de tests que no se
> corrieron.** Si algo no se pudo verificar, dilo explícitamente.

Este paso es enteramente sobre mostrar números de dinero. Un número inventado acá
no es un defecto de estilo: es la funcionalidad entera siendo mentira.

## Lo que ya existe

- `IMeetingHistoryStore.GetCostSummaryAsync()` devuelve
  `CostSummary(SessionCount, ReportCount, TotalCostUsd, FirstReportUtc,
  LastReportUtc)`.
- `IMeetingHistoryStore.GetPromptUsageAsync()` devuelve una lista de
  `PromptUsageSummary(PromptId, PromptVersion, ReportCount, TotalCostUsd,
  AverageOutputTokens, LastUsedUtc)`, agrupada por `prompt_id` + `prompt_version`
  y ordenada por uso.
- El costo se guarda en **micro-dólares enteros** (`cost_micro_usd`), no en
  `REAL`: guardar dinero en punto flotante es pedir deriva justo en la métrica
  que se quiere sumar. `SqliteValueConversions` hace la conversión en un solo
  lugar y el autotest comprueba que `0.000434m` vuelve exacto.
- `--verify-db-selftest` los toca, pero **apenas**: dos comprobaciones, sobre una
  base con **un** reporte. No confundas "está probado" con "está probado en
  serio" — ver la sección de verificación.

---

## Los dos problemas de fondo. Léelos antes de diseñar la pantalla

El criterio de salida dice *"Puedes ver cuánto llevas gastado, real y
acumulado"*. Hoy, tal como está el código, ese número **no se puede calcular**.
No es una opinión: son dos huecos concretos, los dos verificados en el código, y
los dos hay que resolverlos o declararlos en pantalla.

### Problema 1: el costo de transcripción es siempre `null`

`MeetingPipeline` guarda el transcript con `CostUsd: null`, **a propósito**, y lo
dice en un comentario en `src/MeetingAssistant.Core/Abstractions/MeetingPipeline.cs`:

> `CostUsd` va en null porque hoy nadie calcula el costo de transcripción:
> `ICostEstimator` sólo cubre el LLM. Preferible null a un cero que parezca
> medido.

Esa decisión fue correcta al escribirla, y es exactamente la que este paso tiene
que resolver o declarar. Consecuencia: **Deepgram no aparece en ningún total.**
`GetCostSummaryAsync` ya suma `transcript.cost_micro_usd`, pero esa columna está
vacía en todas las filas, así que hoy suma cero y el "total" es sólo el LLM.

Y el LLM es la mitad barata. Deepgram cobra por minuto de audio; una reunión de
una hora puede costar más en transcripción que en extracción. Un panel que diga
"Total gastado: US$0,0043" cuando en realidad gastaste varios dólares **es peor
que no tener panel**.

Tienes dos salidas, y las dos son aceptables. Lo que no es aceptable es mostrar
un total sin calificar:

- **(a) Calcular el costo de transcripción.** Los datos están:
  `session.duration_seconds` se guarda, y `transcript.provider` dice "Deepgram".
  El precio va **en configuración**, siguiendo el patrón que ya existe: la
  sección `Pricing:` que lee `ConfigPricingCostEstimator`, con una clave nueva
  por minuto. **No inventes el precio de Deepgram Nova-3.** No lo pongas
  "provisional" ni "aproximado" en el código ni en un `appsettings.example.json`
  — hay un incidente previo por meter un valor real "sólo para probar" en un
  archivo de ejemplo. Deja la clave documentada con un marcador, y que el usuario
  ponga la cifra de su plan. Si la clave no está, el costo de transcripción es
  desconocido, y eso se **muestra** como desconocido.
- **(b) No calcularlo, y etiquetar el número por lo que es:** "Costo de LLM
  acumulado", con una línea que diga que la transcripción no está incluida y por
  qué. Es menos, pero es honesto y se hace en una tarde.

Elige, impleméntalo y **escribe la decisión en el roadmap**. Si eliges (a) y el
usuario no ha puesto el precio todavía, la pantalla tiene que comportarse como en
(b) hasta que lo ponga.

### Problema 2: un precio que falta produce un cero silencioso

`ConfigPricingCostEstimator.EstimateCostUsd` devuelve **`0m`** cuando no hay
precio configurado para ese proveedor/modelo. Con este comentario:

> Devolver 0 en vez de lanzar — no queremos que falte un precio en el config
> tumbe el pipeline completo de extracción del reporte.

También correcto para el pipeline, y también veneno para un panel de costos: un
reporte con 890 tokens de entrada y 520 de salida puede tener `cost_micro_usd =
0` guardado para siempre, y **nada distingue "costó cero" de "no sabíamos el
precio"**.

Lo bueno es que hay una señal detectable después del hecho: **un reporte con
tokens > 0 y costo = 0 es un reporte sin precio configurado.** No lo escondas —
súbelo a la pantalla: "3 reportes sin precio configurado; no cuentan en el
total". Es la diferencia entre un total que se puede creer y uno que no.

Cambiar el estimador para que lance **no** es la solución: rompería el pipeline
por un dato de configuración, que es justo lo que el comentario evita. Si quieres
distinguirlo en el origen, lo honesto sería guardar `null` en vez de `0` cuando
no hay precio — pero eso toca el pipeline y las filas ya escritas seguirían en
cero. Decide, y si lo dejas como está, muestra el conteo.

### Y un tercero, más chico: esto es estimado, no facturado

El campo se llama `EstimatedCostUsd` y sale de multiplicar tokens por un precio
de configuración. Nadie ha comparado eso contra una factura real de Google, Azure
o Deepgram.

Fase 4 pide "revisar costo real acumulado vs. estimado". Este paso entrega **la
mitad estimada**, que es la que la app puede saber sola. **No etiquetes la
pantalla como "costo real".** Que diga estimado, y que la comparación con la
factura siga siendo un trabajo manual del usuario — anótalo como backlog si te
parece que vale.

---

## Una inconsistencia que hoy es invisible y va a morder después

`CostSummary.TotalCostUsd` suma **reportes + transcripts**.
`PromptUsageSummary.TotalCostUsd` suma **sólo reportes**.

Hoy los dos números coinciden, pero **sólo por accidente**: porque el costo de
transcripción siempre es `null`. El día que alguien resuelva el problema 1 por la
vía (a), el total de arriba dejará de ser la suma de la tabla de abajo, y la
diferencia —Deepgram— no estará explicada en ninguna parte.

Si pones las dos cosas en la misma pantalla, que la relación entre ellas se
entienda: o separas "transcripción" y "LLM" como dos líneas del total, o dices
que la tabla por prompt cubre sólo la parte de LLM. Que los números cuadren a
ojo, o que esté escrito por qué no cuadran.

**Del mismo tipo, más chico:** `CostSummary.SessionCount` cuenta **todas** las
sesiones, incluidas las que fallaron y no produjeron nada. Hoy son 5 de 6. Un
"costo promedio por reunión" que divida por ese número está mal en un factor de
seis. Si muestras promedios, divide por lo que corresponda y di por cuántos.

---

## Dónde vive esto

Decisión tuya, pero con el terreno ya mapeado:

- El `NavigationView` de `MainWindow.xaml` tiene tres items —Grabar, Historial,
  Configuración— con `Tag` y un `switch` en `MainWindow.xaml.cs:44-54`. **Agregar
  un cuarto es trivial** y no arrastra el problema del botón de retroceso, que
  está en `IsBackButtonVisible="Collapsed"`: los items de nivel superior no
  necesitan volver a ningún lado.
- La alternativa es una sección dentro de `HistoryPage`. Sale más barata pero esa
  página ya es un maestro-detalle con búsqueda; meterle un tercer modo la
  convierte en otra cosa.

Lo esperable es una página propia con su `ViewModel` registrado en el contenedor.
Si eliges lo otro, deja escrito por qué.

**No agregues una librería de gráficos.** No hay ninguna en el stack, el corpus
es de decenas de reuniones, y una tabla ordenada con totales dice todo lo que hay
que decir. Un `PackageReference` nuevo en `App` por un gráfico de barras no paga
—y en `Core` está directamente prohibido: `Core.csproj` tiene **0
`PackageReference`** y se queda así.

**Formato de números.** Las fechas usan `SessionListItem.DisplayCulture`
(`es-ES`, fija a propósito); imítalo. Para dinero, el proyecto ya muestra
`US$` con cuatro decimales en la lista de historial y con seis en el mensaje de
estado de la re-extracción. Elige una y sé consistente: con montos de fracciones
de centavo, cuatro decimales redondean a cero cosas que sí costaron algo.

---

## Lo que no puede pasar

**Un fallo de la base no puede tumbar la página ni la app.** Es la regla de la
fase entera, y el precedente sigue siendo T4.4 — una excepción de arranque se
llevó puesta la app y costó nueve días encontrarla. `HistoryViewModel` ya tiene
el patrón: capturar, registrar con `App.LogStartupFailure`, dejar un mensaje y
seguir. Pruébalo apuntando la fábrica de conexiones a una ruta imposible, como
hacen dos de los autotests.

**El estado vacío es el caso que vas a ver.** La base real tiene **0 reportes**
hoy. O sea que lo primero —y quizá lo único— que vas a poder mirar en pantalla es
la pantalla vacía. Que no sea una tabla en blanco: que diga que todavía no hay
reportes y qué hace falta para que aparezcan. Y que **no divida por cero** en
ningún promedio.

---

## Verificación — esto no se cierra leyendo el código

1. `dotnet build MeetingAssistant.sln` en **0 warnings, 0 errores**.
2. Los autotests anteriores siguen en verde: `--verify-render`,
   `--verify-db-selftest`, `--verify-settings-config` (33/33),
   `--verify-reextraction` (18/18), más lo que haya agregado el paso 7. **Si
   tocas el esquema, agrega una migración v2** — no edites el v1.
3. **Extiende `--verify-db-selftest` con la analítica de verdad.** Hoy son dos
   comprobaciones sobre un solo reporte, y eso no ejercita nada de lo que importa
   acá. Es barato —base temporal, sin red, sin proveedores— y es donde se cazan
   los errores de agregación, que son los que nadie ve a ojo. Como mínimo:
   - **varios** reportes, de **varios** prompts y **varias versiones del mismo
     prompt**, y que `GetPromptUsageAsync` agrupe donde debe y no colapse dos
     versiones en una;
   - un reporte con `cost_usd` **null** conviviendo con otros con costo: que el
     total no se vuelva null ni cuente el null como cero sin decirlo;
   - un reporte con **tokens > 0 y costo 0** — el caso del precio faltante — y la
     consulta que lo detecta;
   - `AverageOutputTokens` con un `tokens_output` null en el grupo;
   - la suma en micro-dólares **cuadrando exacto** con varios sumandos: es el
     motivo entero de no usar punto flotante, y hoy sólo se prueba con uno;
   - una base **sin ningún reporte**: `GetCostSummaryAsync` tiene que devolver
     ceros y fechas null sin lanzar.
4. **Usa `dotnet run --project src/MeetingAssistant.App` para probar en la app
   real.** `dotnet build` **no** refresca el layout `AppX\`: lanzar por AUMID
   después de compilar corre el binario viejo sin ningún aviso.
5. **En pantalla, con la base real.** Vas a necesitar reportes, y hoy hay cero.
   Genera dos o tres, **con prompts distintos y al menos dos veces el mismo
   prompt sobre la misma reunión** — que es justo el caso que la comparación
   entre versiones existe para mostrar. La re-extracción desde el historial es la
   forma barata de conseguirlo: ya está construida y probada.

   *Truco para conseguir habla real sin hablar:* reproduce por el altavoz un
   `.wav` de `%LOCALAPPDATA%\MeetingAssistant\meeting-output\` mientras grabas.
   Una grabación en silencio muere en el guard de "transcripción vacía".

   Con eso, comprueba contra `--verify-db` que **los números de la pantalla son
   los de la base**. Esto no es ceremonia: un panel de agregados es exactamente
   donde un `join` de más duplica filas y nadie lo nota, porque el número sigue
   pareciendo un número. Compara a mano al menos un total.
6. Provoca el fallo de base y confirma que la página avisa y la app sigue usable.

**Si una grabación falla con un error que parece de transcripción:** revisa el
consentimiento de micrófono. Desinstalar el paquete lo resetea a `Deny`, el error
aparece al **detener** y no al iniciar, y por el camino HTTP no deja ninguna
línea en el log. Escribir `Allow` en el registro **no alcanza** — hay que
activarlo desde *Configuración > Privacidad y seguridad > Micrófono*.

---

## No lo arregles aquí

- **Cambiar `ICostEstimator` para que cubra transcripción** *puede* ser parte de
  este paso si eliges la vía (a) del problema 1 — pero es lo único del pipeline
  que este paso debería tocar. No te lleves por delante la extracción.
- **Los dos pendientes de GUI del usuario** — "Generar reporte nuevo" y el
  Guardar de `SettingsPage` (roadmap, *Pendiente de confirmación manual*). Son
  suyos.
- **Comparar la calidad de los reportes.** Este paso da el lado medible: cuántos,
  cuánto costaron, qué tan largos salen. La calidad sigue siendo juicio humano y
  no hay que fingir que un número la captura.
- **Editar el prompt desde Configuración.** Deuda de Fase 2.
- **La política de retención de `meeting-output\`** y **el hueco de
  `LocalRecordingApiServer` → `IMeetingPipeline`**. Backlog de Fase 4.
- **Vaciar el `appsettings.json` empaquetado**, que sigue con credenciales en
  claro dentro del `.msix` de sólo lectura. Decisión aparte.

---

## Cierre — y cierre de la fase

Este paso cierra Fase 5, así que el trabajo de documentación es mayor que de
costumbre:

1. Actualiza el **paso 8** con lo hecho y **lo que no se pudo verificar**.
2. Repasa el **criterio de salida de la fase completo** y marca lo que
   corresponda. Presta atención al de costos: si elegiste la vía (b) del problema
   1, *"Puedes ver cuánto llevas gastado, real y acumulado"* **no queda cumplido
   del todo**, y decirlo es más valioso que tacharlo. Si dejaste el precio de
   Deepgram sin configurar porque no ibas a inventarlo, eso es exactamente lo que
   hay que escribir.
3. Deja anotado en el roadmap qué decidiste sobre el costo de transcripción y
   sobre los ceros por precio faltante. Son decisiones que el próximo que mire
   estos números va a necesitar, y no se deducen de la pantalla.
4. Revisa si sigue teniendo sentido el riesgo anotado *"El `.db` pasa a ser lo
   más valioso del sistema y no hay respaldo de nada"*. Con la fase cerrada, la
   base ya tiene transcripts, configuración y credenciales cifradas. Si te parece
   que merece una fase o una tarea propia, dilo — no lo resuelvas acá.

En este proyecto el valor de los documentos es que se les pueda creer: un item
dado por bueno sin medirlo vale menos que uno marcado como no verificado.

Commitea en `main`. **Puedes hacer push**: el usuario trabaja con el repo
sincronizado. Antes de pushear, comprueba que no se cuela nada — el
`appsettings.json` real, `.pfx`/`.cer`, la ruta del vault, el nombre del
empleador, o **un precio real que hayas puesto en un archivo de ejemplo**. Hay un
incidente previo por eso, documentado en `AGENTS.md`.
