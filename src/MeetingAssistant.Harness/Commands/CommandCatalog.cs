/// <summary>
/// Todos los comandos del harness. Lo que dice "Offline" no sale a la red ni
/// toca nada real, y es lo que corre la CI.
/// </summary>
internal static class CommandCatalog
{
    public static IReadOnlyList<HarnessCommand> All { get; } =
    [
        // --- Autotests offline (los corre la CI) --------------------------------
        HarnessCommand.Offline("--verify-render", "Offline. El catalogo de prompts y el render a Markdown.", RenderSelfTest.Run),
        HarnessCommand.Offline("--verify-db-selftest", "Offline. Esquema y store de SQLite, sobre una base temporal.", DatabaseSelfTest.Run),
        HarnessCommand.Offline("--verify-settings-config", "Offline. Las capas de configuracion.", SettingsConfigurationSelfTest.Run),
        HarnessCommand.Offline("--verify-reextraction", "Offline. Cada reporte cae en la reunion que corresponde.", ReextractionSelfTest.RunAsync),
        HarnessCommand.Offline("--verify-archive", "Offline. La cola de subidas a la nube.", ArchiveSelfTests.VerifyArchiveAsync),
        HarnessCommand.Offline("--verify-migration", "Offline. La migracion de SQLite a otro store.", MigrationCommands.VerifyMigrationAsync),
        HarnessCommand.Offline("--verify-retention", "Offline. La retencion de audio local.", RetentionCommands.VerifyAsync),
        HarnessCommand.Offline("--verify-retry", "Offline. Los reintentos al abrir Postgres.", RetryCommands.VerifyAsync),

        // --- Autotests que salen a la red, en recursos descartables -------------
        new("--verify-prompts", "--verify-prompts",
            "SQLite temporal y, si hay Postgres, un esquema descartable. Devuelve 2 si no pudo probar Postgres.", 0, 0,
            context => PromptCommands.VerifyAsync(context.Configuration)),
        new("--verify-postgres", "--verify-postgres",
            "El contrato del store contra SQLite temporal y Postgres, en un esquema que borra al terminar.", 0, 0,
            context => StoreContractTests.VerifyPostgresAsync(context.Configuration)),
        new("--verify-cloud-settings", "--verify-cloud-settings",
            "Los ajustes en la nube, en un esquema descartable.", 0, 0,
            context => CloudSettingsCommands.VerifyAsync(context.Configuration)),
        new("--verify-blob-upload", "--verify-blob-upload",
            "Sube dos archivos chicos a Azure Storage, los lee y los borra.", 0, 0,
            context => ArchiveSelfTests.VerifyBlobUploadAsync(context.Configuration)),
        new("--verify-pipeline-history", "--verify-pipeline-history <wav> [--prompt <id>]",
            "El pipeline completo contra una base temporal. Cobra (Deepgram y LLM).", 1, 1,
            context => PipelineHistorySelfTest.RunAsync(context.Arguments[0], context.PromptId)),

        // --- Lo REAL ----------------------------------------------------------
        new("--verify-db", "--verify-db",
            "Mira la base local y el historial en Postgres de verdad, sin cambiar nada.", 0, 0,
            async context =>
            {
                int local = DatabaseInspection.InspectLocal();
                int postgres = await PostgresInspection.RunAsync(context.Configuration);
                return Math.Max(local, postgres);
            }),
        new("--set-setting", "--set-setting <clave> [valor]",
            "Escribe un ajuste en la base local REAL (sin valor, lo borra).", 1, 2,
            context => Task.FromResult(SettingCommands.Set(context.Arguments[0], context.Arguments.ElementAtOrDefault(1)))),
        new("--list-cloud-settings", "--list-cloud-settings",
            "Lista los ajustes REALES en la nube.", 0, 0,
            context => CloudSettingsCommands.ListAsync(context.Configuration)),
        new("--set-cloud-setting", "--set-cloud-setting <clave> [valor]",
            "Escribe un ajuste REAL en la nube (sin valor, lo borra).", 1, 2,
            context => CloudSettingsCommands.SetAsync(context.Configuration, context.Arguments[0], context.Arguments.ElementAtOrDefault(1))),
        new("--audio-retention", "--audio-retention <dias> [--apply]",
            "Que WAV locales se borrarian, comprobando contra la nube real. Solo con --apply borra.", 1, 2,
            context => RetentionCommands.RunFromArgumentsAsync(context.Configuration, context.Arguments)),
        new("--migrate-to-postgres", "--migrate-to-postgres [--apply]",
            "Copia el historial local a Postgres y Azure Blob. Sin --apply solo simula.", 0, 1,
            context => MigrationCommands.MigrateFromArgumentsAsync(context.Configuration, context.Arguments)),

        // --- El pipeline de verdad (cobra) -------------------------------------
        PipelineRunCommand.TranscribeFile,
        PipelineRunCommand.ProcessFile,
        PipelineRunCommand.ExtractTranscript,
    ];

    public static HarnessCommand? Find(string name) =>
        All.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));
}
