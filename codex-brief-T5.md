# Brief para Codex — T5 (autostart opcional al arrancar Windows)

Generado el 2026-08-25, despues del commit `75d025b`. El plan detallado sigue
viviendo en `TASK_GRAPH.md`; este archivo es la orden de trabajo.

---

Trabaja en el repo `D:\stuffProjectsCH\consoleApp_MeetingsAssistant`
(rama `main`, sincronizada con `origin/main`).

Antes de escribir codigo lee `AGENTS.md` completo, la seccion `## T5 — Optional
autostart on Windows boot` de `TASK_GRAPH.md`, y las notas de validacion de T6a
(en la seccion de T6) — de ahi sale la identidad de paquete contra la que vas a
validar. Las reglas de `AGENTS.md` son obligatorias: `MeetingAssistant.Core` no
gana referencias de plataforma ni de proveedor, el `appsettings.json` real no se
commitea, y nada se marca como hecho sin haberlo corrido de verdad.

## Estado del que partis

T6a quedo cerrada: hay un MSIX x64 firmado e **instalado**, un solo paquete
registrado, `IsDevelopmentMode=False`, PFN
`962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4`. Eso es lo que hace que T5
sea validable: `RequestEnableAsync()` y los estados `DisabledByUser` /
`DisabledByPolicy` necesitan identidad de paquete real.

Sigue abierto y **no es tuyo** (cae en T6b): los toasts de `TranscriptReady` y
`ReportSaved`, el clic real en un toast, y el label de la bandeja despues de un
`POST /recording/start`.

## Leelo antes de correr nada: `dotnet run` te va a desinstalar T6a

Esto es nuevo y no le paso a nadie todavia, asi que no hay cicatriz en el repo
que te avise.

Hasta T6a el manifiesto tenia `Publisher="CN=AppPublisher"`, distinto del
paquete instalado, asi que el registro de desarrollo y el instalado eran dos
paquetes. **Ya no.** El manifiesto ahora dice
`CN=MeetingAssistant Local Publisher`, el mismo Subject del certificado, o sea
la **misma identidad** que el MSIX instalado. Consecuencia: un `dotnet run`
registra un paquete de desarrollo con el mismo PackageFamilyName y **reemplaza
la instalacion firmada de T6a**. `Get-AppxPackage` va a volver a decir
`IsDevelopmentMode=True`.

No es un desastre, es un ciclo de trabajo: itera con `dotnet run` todo lo que
necesites, pero **al terminar T5 reinstala el MSIX firmado** y decilo en las
notas de validacion. Si no, T6b arranca creyendo que valida el paquete firmado
y en realidad valida un registro de desarrollo.

Corolario molesto: **el log de diagnostico se mueve segun como este registrada
la app.** Con el MSIX instalado esta en la ruta plana
`%LOCALAPPDATA%\MeetingAssistant\startup-errors.log` (medido en T6a). Con
registro de desarrollo la escritura se redirige y aparece en
`%LOCALAPPDATA%\Packages\962A0BC5-A1BC-432A-8A38-55011BFE3EE0_n5p1q6rt9wnn4\LocalCache\Local\MeetingAssistant\`.
Mismo PFN, dos ubicaciones posibles. **Mira las dos** antes de concluir que algo
no dejo rastro: buscar el log en la ruta equivocada ya costo dos dias en T4.3.

## La trampa que este proyecto ya piso, y que T5 puede repetir exacta

`RecordViewModel` mostraba estado viejo porque tenia campos locales
inicializados a `false` y nunca leia de `IMeetingPipeline.IsRecording`, la
fuente de verdad. Costo T1 y T2.1 arreglarlo.

Un toggle de autostart tiene **el mismo defecto disponible**: si lo inicializas
en `false` y no lees `StartupTask.State` al entrar a la pagina, la primera vez
que el usuario desactive el autostart desde Configuracion de Windows tu toggle
va a seguir diciendo "encendido". Y esa divergencia es peor aca que en
`RecordPage`, porque el usuario no tiene forma de notarla hasta que la maquina
no arranca la app.

Lee el estado real en cada `Loaded`, no solo en el constructor.

## Implementacion

1. **`Windows.ApplicationModel.StartupTask`**, no `HKCU\...\Run` ni un acceso
   directo en la carpeta de Inicio. La app esta empaquetada; esos dos mecanismos
   son los equivocados y ademas se saltan el toggle y el consentimiento de
   Configuracion de Windows > Aplicaciones de inicio.
2. **Manifiesto** (`src/MeetingAssistant.App/Package.appxmanifest`): agrega el
   namespace `uap5` — hoy declara `uap rescap systemai com desktop`, hay que
   sumarlo al `xmlns:` **y** a `IgnorableNamespaces`. Despues agrega
   `<uap5:Extension Category="windows.startupTask">` con un `StartupTaskId` y
   `Enabled="false"`. **Arranca deshabilitado siempre**: el roadmap dice
   "Autostart opcional", asi que es opt-in y nunca se enciende solo.
   Ya hay un bloque `<Extensions>` dentro de `<Application>` con el
   `windows.comServer` y el `windows.toastNotificationActivation` de T4.3 —
   la entrada nueva va ahi, no en un bloque nuevo, y **no toques esas dos**: son
   lo que hace que los toasts funcionen.
3. **Servicio nuevo** en `src/MeetingAssistant.App/Services/`, registrado como
   singleton en `ConfigureServices` (`App.xaml.cs`; el patron esta a la vista con
   `ActivityNotificationService`, `TrayIconService`, `GlobalHotkeyService`). El
   `StartupTaskId` se define en un solo lugar y lo comparten manifiesto y codigo;
   si se desincronizan, `GetAsync` tira y el sintoma no dice por que.
4. **UI: un solo `ToggleSwitch` en `SettingsPage`.** Hoy es un stub con un
   `TextBlock` que dice "Próximamente" (`Views/SettingsPage.xaml`), y la
   navegacion ya lo tiene cableado (`MainWindow.xaml.cs:50`, tag `settings`).
   Las paginas resuelven sus dependencias con
   `App.Services.GetRequiredService<T>()` en el constructor — segui ese patron,
   mira `RecordPage.xaml.cs:17`.
5. **Manejar los estados, no solo el on/off.** `StartupTaskState` tiene
   `Disabled`, `DisabledByUser`, `DisabledByPolicy`, `Enabled` y
   `EnabledByPolicy`. `DisabledByUser` y `DisabledByPolicy` **no se pueden
   revertir desde la app**: `RequestEnableAsync()` devuelve el mismo estado y no
   pasa nada. Ahi el toggle tiene que quedar apagado **y explicar por que**, con
   el texto diciendo que se cambia desde Configuracion de Windows. Un toggle que
   no reacciona y no explica nada es el mismo fallo silencioso que T4 existe para
   eliminar.
6. **Cuidado con el `Toggled` durante la inicializacion.** Si seteas `IsOn` en
   codigo para reflejar el estado leido, el handler se dispara y llama a
   `RequestEnableAsync()` solo. Poner una guarda; es el bug clasico de este
   control.
7. `RequestEnableAsync()` muestra un dialogo de consentimiento la primera vez y
   tiene que correr en el hilo de UI.

## Alcance: solo el toggle

`SettingsPage` y `HistoryPage` son stubs de **Fase 2**, no de Fase 3.
`TASK_GRAPH.md` lo dice explicitamente para que nadie se expanda: T5 agrega
**un control**, no la pagina de configuracion. Nada de vault path, `SubFolder`,
API keys ni edicion de prompts. Si te tienta, es senal de que te estas saliendo
del alcance.

## No lo arregles aqui

- `LocalRecordingApiServer` llama a `IMeetingPipeline` directo y no levanta los
  eventos del coordinador: una grabacion por HTTP no actualiza `RecordPage` ni
  produce toast. Backlog.
- El `<com:ExeServer DisplayName="MeetingAssistant.App">` del manifiesto quedo
  con el nombre viejo despues de que T6a renombro los demas `DisplayName` a
  "Meeting Assistant". Es el display name del servidor COM y casi no se
  superficie. Si lo alineas, que sea en un cambio aparte y dicho en el commit;
  no lo mezcles con T5.

## Trampa de build, ya mordio tres veces

Con la app corriendo, `dotnet build` reporta exito pero **no actualiza el layout
`AppX\`**: el proceso vivo tiene el `.exe` y el `.dll` tomados. Cerra la app
antes de compilar (bandeja > Salir; si la bandeja no esta disponible, matar el
proceso y verificar que no quede) y recien despues compila.

## Criterios de aceptacion

- El toggle en "Configuración" enciende el autostart y aparece registrado en
  `Administrador de tareas > Aplicaciones de inicio`. Apagarlo lo quita. Esto se
  verifica **de verdad**, no por inspeccion de codigo.
- Con la app deshabilitada a nivel de SO desde Configuracion de Windows, el
  toggle refleja `DisabledByUser` y muestra la explicacion — no un "encendido"
  enganoso.
- Entrar a la pagina, cambiar el estado desde Configuracion de Windows, volver a
  entrar: el toggle muestra el estado real, no el viejo.
- El manifiesto declara `Enabled="false"`: una instalacion nueva no arranca con
  Windows sin que nadie lo pida.
- `dotnet build MeetingAssistant.sln -t:Rebuild` con 0 errores y 0 warnings.
  Hoy la solucion esta en 0/0; si aparece un warning, es tuyo.
- **El MSIX firmado reinstalado al terminar**, con `Get-AppxPackage` mostrando un
  solo paquete e `IsDevelopmentMode=False`. Si iteraste con `dotnet run`, esto no
  es opcional.

Lo que **no** es criterio de T5: re-verificar T2-T4 contra la instalacion. Eso es
T6b.

## Cierre

Actualiza `TASK_GRAPH.md`: estado de T5 en la tabla de `## Status` y en la tabla
de orden de cierre, y notas de validacion con lo que **realmente** corriste —
incluido si iteraste con registro de desarrollo y si reinstalaste el MSIX. Si
algo no se pudo probar de verdad, decilo explicitamente en lugar de rellenarlo.

Despues de T5 solo queda **T6b**, que cierra Fase 3: instalar el paquete de
verdad y re-verificar T2-T5 completos sobre esa instalacion, mas desinstalacion
limpia. Ojo con un criterio de T6b que se agrego despues de T6a: la
desinstalacion tiene que sacar tambien el certificado de
`LocalMachine\TrustedPeople`, que Configuracion de Windows no revierte.

Commitea en `main` con un mensaje que explique el porque, no solo el que. No
hagas push: el usuario lo revisa antes.
