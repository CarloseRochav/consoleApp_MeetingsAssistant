# Brief para Codex — T3 (hotkey global)

Generado el 2026-08-21, despues del commit `afa9adc` (T2.2 + T2.1). El plan
detallado sigue viviendo en `TASK_GRAPH.md`; este archivo es solo la orden de
trabajo que se le entrega a Codex.

---
Trabaja en el repo `C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant` (rama `main`, ya sincronizada con `origin/main` en el commit `afa9adc`).

Antes de escribir codigo lee `AGENTS.md` completo y la seccion `## T3 — Global hotkey to start/stop recording` de `TASK_GRAPH.md`, ademas de `## T2.2` (recien agregada) para entender el estado de la bandeja y de la sincronizacion de estado. Las reglas de `AGENTS.md` son obligatorias, en particular: `MeetingAssistant.Core` no puede ganar ninguna referencia especifica de plataforma o proveedor, `appsettings.json` real nunca se commitea, y nada se marca como hecho sin haberlo corrido de verdad.

## Paso 0 — Validacion pendiente (bloquea T3, no lo saltes)

El commit `afa9adc` dejo dos cambios implementados pero SIN validacion grafica, porque la sesion que los hizo no tenia acceso a GUI:

1. `Assets/TrayIcon.ico` (nuevo, un solo frame, DIB/BMP 32x32) reemplaza al PNG en `Services/TrayIconService.cs`.
2. `ViewModels/RecordViewModel.cs` ahora se suscribe a `StateChanged`, `RecordingCompleted` y `RecordingFailed` del `RecordingCoordinator`.

Antes de tocar nada:

- Cierra cualquier proceso `MeetingAssistant.App` que haya quedado vivo de sesiones anteriores (`Get-Process MeetingAssistant.App`). Si hay uno, confirma con el usuario antes de matarlo: puede tener una grabacion en curso.
- Lanza `dotnet run --project src/MeetingAssistant.App` y verifica:
  - el icono aparece en el area de notificacion;
  - `startup-errors.log` (junto al exe, ver T7 para la ruta exacta) queda limpio;
  - los cuatro criterios de aceptacion de T2.1 en `TASK_GRAPH.md`: iniciar desde la bandeja con la ventana oculta y abrirla debe mostrar "Detener grabacion" / "Grabando..."; detener desde la bandeja debe dejar la ruta del reporte y el transcript en la pagina; un fallo forzado debe verse en `StatusMessage`/`ErrorDetails`; y el flujo normal desde el boton de RecordPage no debe haber cambiado.

Si el icono sigue sin aparecer, para y reporta: el stack trace ahora si llega a `startup-errors.log` gracias a los handlers globales de T7. No sigas con T3 encima de una bandeja rota — T3 depende de que la app siga viva sin ventana visible.

Si todo pasa, actualiza en `TASK_GRAPH.md` el estado de T2 y T2.2 de 🟡 a ✅ con las notas de lo que realmente probaste.

## Paso 1 — T3, hotkey global

Sigue la implementacion descrita en `## T3` de `TASK_GRAPH.md`. Resumen de lo que se espera:

- Sin paquete NuGet nuevo: interop escrito a mano (`RegisterHotKey`, `UnregisterHotKey`, `WM_HOTKEY`), consistente con la preferencia del repo por superficie minima (ver el comentario de `LocalRecordingApiServer` sobre `HttpListener` vs Kestrel).
- El `HWND` sale de `WinRT.Interop.WindowNative.GetWindowHandle(window)`.
- WinUI 3 no expone `WndProc`. Resuelve el patron correcto en el momento de implementar (lo habitual es `SetWindowSubclass`/`ComCtl32` con un `SUBCLASSPROC`, manteniendo viva la referencia al delegado para que el GC no lo recoja). Verifica el patron que elijas, no lo asumas.
- El hotkey llama exactamente al mismo toggle de `RecordingCoordinator` que usan el menu de bandeja y el boton de RecordPage. No dupliques logica de grabacion.
- Configurable por `appsettings.json` con las claves `Hotkey:Modifiers` y `Hotkey:Key`, siguiendo el mismo patron de `IConfiguration` que ya usan `Api:Port` / `Api:AuthToken`. Agrega los placeholders correspondientes a `appsettings.example.json` — sin valores reales de ningun tipo.
- **Default decidido por el usuario: `Ctrl+Shift+R`.** Nota para el reporte, no para cambiarlo por tu cuenta: `RegisterHotKey` captura la combinacion a nivel de sistema, asi que mientras la app este corriendo `Ctrl+Shift+R` deja de llegar al navegador (recarga forzada) y a otros editores. Si al probarlo resulta molesto, la salida es cambiar el valor en `appsettings.json`, no hardcodear otro default.
- `UnregisterHotKey` en la salida real por "Salir" de la bandeja, de forma deterministica: un registro filtrado sobrevive al proceso en algunas versiones de Windows.
- Si `RegisterHotKey` falla porque la combinacion ya esta tomada, la app NO puede caerse ni tragarse el error en silencio: registralo en el log y hazlo visible (tooltip de la bandeja o notificacion de H.NotifyIcon; el toast formal es T4, no lo adelantes).

## Criterios de aceptacion

- Con la ventana oculta, el hotkey inicia la grabacion; presionarlo de nuevo la detiene y la procesa, igual que el menu de bandeja o el boton de RecordPage.
- Registrar la misma combinacion desde otra app ya corriendo no tumba esta app al arrancar: el fallo de `RegisterHotKey` se captura y se hace visible.
- Despues de un "Salir" limpio no queda registro filtrado: la misma combinacion puede registrarse desde otro proceso de prueba.
- Con la ventana abierta, un start/stop por hotkey se refleja en RecordPage sin quedar en estado viejo (esto es lo que T2.1 acaba de habilitar; si falla, es un bug de T2.1, reportalo en lugar de parcharlo en el servicio de hotkey).
- `dotnet build MeetingAssistant.sln` sin errores.

## Cierre

Actualiza `TASK_GRAPH.md`: estado de T3, notas de validacion con lo que realmente corriste, y la pregunta abierta del hotkey marcada como resuelta (`Ctrl+Shift+R`, decidido por el usuario el 2026-08-21). Si algo no se pudo probar de verdad, dilo explicitamente en las notas en lugar de rellenarlo. Luego commitea en `main` con un mensaje que explique el porque, no solo el que.
