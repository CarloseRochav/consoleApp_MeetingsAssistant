/// <summary>
/// Todos los comandos del harness. Lo que dice "Offline" no sale a la red ni toca nada real, y es lo
/// que corre la CI. Desde el paso B de la Fase 8 (2026-11-10) el escritorio es cliente de la API de
/// DevLog: los autotests que llamaban a Deepgram, al LLM, a Postgres o a Blob se reemplazaron por
/// autotests contra la API (con un transporte falso offline, y de verdad con --verify-api).
/// </summary>
internal static class CommandCatalog
{
    public static IReadOnlyList<HarnessCommand> All { get; } =
    [
        // --- Autotests offline (los corre la CI) --------------------------------
        HarnessCommand.Offline("--verify-api-client", "Offline. El cliente HTTP de la API: formas, errores (401, 429 con números, 5xx), sin secretos en ToString.", ApiSelfTests.VerifyClientAsync),
        HarnessCommand.Offline("--verify-block-upload", "Offline. La subida por bloques a Storage: ids, reanudar, 403, cortes.", ApiSelfTests.VerifyBlockUploadAsync),
        HarnessCommand.Offline("--verify-wav-convert", "Offline. La conversión a 16 kHz 16 bits mono antes de subir.", ApiSelfTests.VerifyConversionAsync),
        HarnessCommand.Offline("--verify-device-auth", "Offline. Inicio de sesión del dispositivo, DPAPI, revocado.", ApiSelfTests.VerifyDeviceAuthAsync),
        HarnessCommand.Offline("--verify-upload-queue", "Offline. La cola: sin red, reinicio a mitad, 429, revocado, re-extracción.", ApiSelfTests.VerifyQueueAsync),
        HarnessCommand.Offline("--verify-api-services", "Offline. Costos (nulo nunca es 0), prompts y ajustes permitidos.", ApiSelfTests.VerifyServices),
        HarnessCommand.Offline("--verify-local-retention", "Offline. El WAV original se borra N dias despues de su reporte, y nada mas.", LocalRetentionSelfTest.Run),
        HarnessCommand.Offline("--verify-settings-config", "Offline. Las capas de configuracion local.", SettingsConfigurationSelfTest.Run),
        HarnessCommand.Offline("--verify-release", "Offline. El .appinstaller y el comando de instalacion.", ReleaseCommands.Verify),

        // --- Offline pero pesado (no va en la CI) --------------------------------
        new("--verify-long-audio", "--verify-long-audio [minutos]",
            "Offline. Una grabacion larga sintetica (110 min por defecto) convertida: tamano, tiempo, entra en 1 GiB. Escribe cientos de MB en %TEMP%.", 0, 1,
            context => ApiSelfTests.VerifyLongAudioAsync(context.Arguments)),

        new("--verify-capture", "--verify-capture [segundos]",
            "Graba N segundos con la captura de la app (altavoz + micro) y dice cuantos bytes salieron. No sube nada.", 0, 1,
            context => CaptureCheck.RunAsync(context.Arguments)),

        // --- La API de DevLog de verdad -------------------------------------------
        new("--api-signin", "--api-signin <usuario>",
            "Inicia sesion del harness (dispositivo harness-MAQUINA); pide la contrasena sin mostrarla y no la guarda.", 1, 1,
            context => ApiCommands.SignInAsync(context.Configuration, context.Arguments[0])),
        new("--api-signout", "--api-signout",
            "Borra el token del harness en esta maquina (no lo revoca).", 0, 0,
            _ => Task.FromResult(ApiCommands.SignOut())),
        new("--verify-api", "--verify-api",
            "Autotest de solo lectura contra la API REAL con el token del harness: sesiones, transcript, busqueda, costos, prompts, ajustes, audio, 401.", 0, 0,
            context => ApiCommands.VerifyAsync(context.Configuration)),
        new("--api-process-file", "--api-process-file <wav> [--vault <carpeta>]",
            "Un WAV por el camino de la app (16 kHz, cola, servidor, .md). COBRA. Vault de prueba: local-vault/.", 1, 3,
            context => ApiCommands.ProcessFileAsync(context.Configuration, context.Arguments)),
        new("--api-resume", "--api-resume [--vault <carpeta>]",
            "Retoma lo pendiente de la cola del harness (tras un corte o un proceso matado). Puede COBRAR.", 0, 2,
            context => ApiCommands.ResumeAsync(context.Configuration, context.Arguments)),
        new("--api-reextract", "--api-reextract <idSession> <promptId> [--vault <carpeta>]",
            "Re-extrae una sesion real con otro prompt (Jobs opcion 2). COBRA el LLM.", 2, 4,
            context => ApiCommands.ReextractAsync(context.Configuration, context.Arguments)),

        // --- Lo REAL de esta maquina y del dueno -----------------------------------
        new("--verify-db", "--verify-db",
            "Mira la base local de ajustes de verdad, sin cambiar nada.", 0, 0,
            _ => Task.FromResult(DatabaseInspection.InspectLocal())),
        new("--set-setting", "--set-setting <clave> [valor]",
            "Escribe un ajuste en la base local REAL (sin valor, lo borra).", 1, 2,
            context => Task.FromResult(SettingCommands.Set(context.Arguments[0], context.Arguments.ElementAtOrDefault(1)))),
        new("--publish-release", "--publish-release <carpeta> [--apply]",
            "Sube el instalador (la carpeta que deja installer/Build-Release.ps1) a Azure Blob, contenedor privado con enlace de solo lectura. Sin --apply solo simula.", 1, 2,
            context => ReleaseCommands.PublishFromArgumentsAsync(context.Configuration, context.Arguments)),
        new("--migrate-to-devlog", "--migrate-to-devlog <idUser> [--apply]",
            "La importacion final: compara la base vieja con DevLog fila por fila; sin --apply solo cuenta, y nunca aplica si pisaria una fila de DevLog.", 1, 2,
            context => DevLogMigrationCommands.MigrateFromArgumentsAsync(context.Configuration, context.Arguments)),
    ];

    public static HarnessCommand? Find(string name) =>
        All.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));
}
