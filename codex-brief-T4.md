# Brief para Codex — T4 (toast de reporte listo / de fallo)

Generado el 2026-08-22, despues del commit `8891877`. El plan detallado sigue
viviendo en `TASK_GRAPH.md`; este archivo es la orden de trabajo.

---

Trabaja en el repo `C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir codigo lee `AGENTS.md` completo, la seccion
`## T4 — Toast notification when the report is ready (and on failure)` de
`TASK_GRAPH.md`, y la seccion `## Orden de cierre de Fase 3 (replanificado
2026-08-22)` para ubicar donde encaja esto. Las reglas de `AGENTS.md` son
obligatorias: `MeetingAssistant.Core` no gana referencias de plataforma ni de
proveedor, el `appsettings.json` real no se commitea, y nada se marca como hecho
sin haberlo corrido de verdad.

## Por que esta tarea importa mas de lo que parece

No es una mejora cosmetica. Hoy, si el pipeline falla con la ventana oculta, el
error no tiene ninguna superficie visible: muere en
`RecordViewModel.StatusMessage`, que nadie ve si `RecordPage` no esta abierto.
T3 empeoro esto sin querer — con el hotkey global, grabar sin abrir la ventana
ya es el camino normal. Un fallo silencioso significa una reunion que el usuario
cree capturada y no lo esta, que es exactamente lo que esta herramienta existe
para evitar. El toast de **fallo** es la parte critica; el de exito es
conveniencia.

## Implementacion

1. Sin paquete NuGet nuevo. `AppNotificationManager`
   (`Microsoft.Windows.AppNotifications`) viene en `Microsoft.WindowsAppSDK`,
   ya referenciado en `2.3.1`. Si el namespace no resuelve, eso es senal de
   subir la version del WindowsAppSDK — no de caer a una libreria de toasts
   legacy.
2. `AppNotificationManager.Default.Register()` una sola vez al arrancar, en
   `App.xaml.cs`, y `Unregister()` en la salida real. Los puntos exactos ya
   existen y estan emparejados: `_apiServer.Start()` esta en `LaunchCore()`
   (`App.xaml.cs:91`, llamado desde `OnLaunched`) y `_apiServer.Stop()` en `ExitApplicationAsync()`
   (`App.xaml.cs:264`, junto a `_globalHotkeyService?.Dispose()` y
   `_trayIconService?.Dispose()`). Segui ese mismo patron y ese mismo orden.
3. El registro tiene que ir envuelto igual que el resto del arranque: si falla,
   se loguea con `App.LogStartupFailure(...)` y la app sigue viva sin toasts.
   Una notificacion es conveniencia, no dependencia critica — el precedente es
   el `try/catch` alrededor de `_trayIconService.AttachTo(_window)`.
4. Suscribite a `RecordingCoordinator.RecordingCompleted` y
   `RecordingFailed` (`Services/RecordingCoordinator.cs`). Ya hay dos
   suscriptores a estos eventos, `TrayIconService` y `RecordViewModel`; mira
   como lo hacen antes de inventar un patron nuevo. Importante:
   `RaiseRecordingCompleted` / `RaiseRecordingFailed` ya envuelven cada handler
   en su propio `try/catch`, asi que un fallo tuyo no rompe el pipeline — pero
   no te apoyes en eso para no manejar tus propios errores.
5. Contenido:
   - Exito: la ruta del reporte guardado. Un boton "Abrir en Explorador" es
     lindo de tener, no requisito; si lo haces, reusa lo que ya hace
     `RecordViewModel.OpenSavedReport`.
   - Fallo: el mensaje de la excepcion.
   - **Nunca** metas el transcript completo ni contenido de la reunion en el
     cuerpo del toast. El historial de notificaciones de Windows persiste eso
     fuera del vault. La ruta del archivo esta bien; el contenido no.
6. Donde vive el codigo: `src/MeetingAssistant.App/Services/`, un servicio nuevo
   registrado como singleton en `ConfigureServices` (`App.xaml.cs:232` y
   alrededores tienen el patron). No lo cuelgues dentro de `TrayIconService`.

## Hueco conocido, no lo arregles aqui

`LocalRecordingApiServer` llama a `IMeetingPipeline` directo, no al
`RecordingCoordinator`, asi que **una grabacion disparada por HTTP no levanta
estos eventos y por lo tanto no va a producir toast.** Es el mismo hueco de T1
que ya deja `RecordPage` con estado viejo, esta anotado en el backlog del
`TASK_GRAPH.md`, y arreglarlo es rutear el server por el coordinador — mas
grande que esta tarea. Mencionalo en las notas de validacion, no lo parchees.

## Criterios de aceptacion

- Grabacion iniciada por bandeja o por hotkey, ventana oculta, detenida: aparece
  un toast de Windows con la ruta del reporte guardado, sin ninguna ventana
  visible.
- Fallo forzado (por ejemplo una API key invalida temporalmente) con la ventana
  cerrada: aparece un toast de error. **Este es el caso que importa** — hoy no
  tiene ninguna retroalimentacion visible.
- El toast aparece atribuido correctamente a la app (nombre e icono). Esto
  depende de identidad de paquete: verificalo bajo `dotnet run` y anota que la
  verificacion definitiva es contra el MSIX instalado de T6b.
- `Unregister()` corre en la salida por "Salir" de la bandeja, no en el cierre
  de ventana (que solo oculta).
- `dotnet build MeetingAssistant.sln -t:Rebuild` sin errores. Usa `-t:Rebuild`:
  un build incremental que se salta `Infrastructure` reporta 0 warnings y oculta
  el `CS0649` preexistente de `LocalRecordingApiServer._cts`, que es exactamente
  el error de reporte que hubo en T3. Ese warning es preexistente y no es tuyo;
  reportalo como tal en vez de decir "0 warnings".

## Cierre

Actualiza `TASK_GRAPH.md`: estado de T4 en la tabla y en la tabla de orden de
cierre, y notas de validacion con lo que **realmente** corriste. Si algo no se
pudo probar de verdad, decilo explicitamente en lugar de rellenarlo. Despues de
T4 sigue T6a (identidad de paquete + firma); antes de generar cualquier
certificado hay que agregar `*.pfx` al `.gitignore`, que hoy no lo cubre.

Commitea en `main` con un mensaje que explique el porque, no solo el que. No
hagas push: el usuario lo revisa antes.
