using MeetingAssistant.Core.Abstractions;
using MeetingAssistant.Core.Models;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Cost;
using MeetingAssistant.Infrastructure.Llm;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.Infrastructure.Storage.Sqlite;
using MeetingAssistant.Infrastructure.Transcription;
using Microsoft.Extensions.Configuration;

const string appSettingsFileName = "appsettings.json";
string? promptId = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--prompt", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        promptId = args[++i];
        continue;
    }

    positional.Add(args[i]);
}

bool processExistingAudio = positional.Count == 2 && string.Equals(positional[0], "--process-file", StringComparison.OrdinalIgnoreCase);
bool extractExistingTranscript = positional.Count == 2 && string.Equals(positional[0], "--extract-transcript", StringComparison.OrdinalIgnoreCase);
string? existingAudioPath = positional.Count == 2 &&
    (string.Equals(positional[0], "--transcribe-file", StringComparison.OrdinalIgnoreCase) || processExistingAudio)
    ? positional[1]
    : null;
string? existingTranscriptPath = extractExistingTranscript ? positional[1] : null;
int durationSeconds = existingAudioPath is null && existingTranscriptPath is null && positional.Count > 0 && int.TryParse(positional[0], out int parsedDuration)
    ? parsedDuration
    : 15;
if (existingAudioPath is null && existingTranscriptPath is null && durationSeconds <= 0)
{
    Console.Error.WriteLine("La duracion de captura debe ser mayor que cero.");
    return 1;
}

if (existingAudioPath is not null && !File.Exists(existingAudioPath))
{
    Console.Error.WriteLine($"No existe el audio indicado: {existingAudioPath}");
    return 1;
}

if (existingTranscriptPath is not null && !File.Exists(existingTranscriptPath))
{
    Console.Error.WriteLine($"No existe el transcript indicado: {existingTranscriptPath}");
    return 1;
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-render", StringComparison.OrdinalIgnoreCase))
{
    return VerifyCatalogAndRender();
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-db", StringComparison.OrdinalIgnoreCase))
{
    return VerifyDatabase();
}

if (positional.Count == 1 && string.Equals(positional[0], "--verify-db-selftest", StringComparison.OrdinalIgnoreCase))
{
    return VerifyDatabaseSelfTest();
}

if (positional.Count == 2 &&
    string.Equals(positional[0], "--verify-pipeline-history", StringComparison.OrdinalIgnoreCase))
{
    return await VerifyPipelineHistoryAsync(positional[1], promptId);
}

try
{
    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(appSettingsFileName, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    IPromptCatalog promptCatalog = new BuiltInPromptCatalog();

    ITranscriptionClient? transcriptionClient = null;
    ITranscriptionClient GetTranscriptionClient() =>
        transcriptionClient ??= new DeepgramTranscriptionClient(ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));

    if (existingAudioPath is not null && !processExistingAudio)
    {
        Console.WriteLine($"=== Transcribiendo archivo existente ==={Environment.NewLine}{existingAudioPath}");
        TranscriptionResult transcription = await GetTranscriptionClient().TranscribeAsync(existingAudioPath);
        Console.WriteLine($"Transcripción recibida: {transcription.Transcript.Length:N0} caracteres; {transcription.Utterances.Count:N0} intervenciones.");
        Console.WriteLine($"Duracion: {transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {transcription.Latency.TotalSeconds:F2} s.");
        return 0;
    }

    ILlmClient llmClient = CreateLlmClient(configuration);
    ILlmReportExtractor reportExtractor = new LlmReportExtractor(
        llmClient,
        new ConfigPricingCostEstimator(configuration),
        promptCatalog);

    if (existingTranscriptPath is not null)
    {
        string chosenPrompt = promptId ?? promptCatalog.Default.Id;
        Console.WriteLine($"=== Extrayendo reporte de transcript ==={Environment.NewLine}{existingTranscriptPath}");
        Console.WriteLine($"Prompt: {chosenPrompt}");
        string transcript = await File.ReadAllTextAsync(existingTranscriptPath);
        ExtractionResult extracted = await reportExtractor.ExtractAsync(transcript, chosenPrompt);
        IReportStorage transcriptStorage = new MarkdownReportStorage(configuration);
        string savedPath = await transcriptStorage.SaveMarkdownAsync(extracted.MarkdownBody, extracted.Metadata);
        Console.WriteLine();
        Console.WriteLine("=== Prompt usado ===");
        Console.WriteLine(extracted.Prompt.SystemPrompt);
        Console.WriteLine();
        Console.WriteLine("=== Reporte generado ===");
        Console.WriteLine(extracted.MarkdownBody);
        if (extracted.StructuredReport is not null)
        {
            PrintReport(extracted.StructuredReport);
        }
        Console.WriteLine($"Reporte guardado: {savedPath}");
        return 0;
    }
    IAudioCaptureService audioCapture = new AudioCaptureService();
    IReportStorage reportStorage = new MarkdownReportStorage(configuration);
    string outputDirectory = Path.Combine(AppContext.BaseDirectory, "meeting-output");
    IMeetingPipeline pipeline = new MeetingPipeline(
        audioCapture,
        GetTranscriptionClient(),
        reportExtractor,
        reportStorage,
        outputDirectory);

    if (existingAudioPath is not null)
    {
        Console.WriteLine($"=== Procesando archivo existente ==={Environment.NewLine}{existingAudioPath}");
        MeetingPipelineResult importedResult = await pipeline.ProcessAudioFileAsync(existingAudioPath);
        Console.WriteLine($"Audio: {importedResult.Audio.AudioPath}");
        Console.WriteLine($"Duración: {importedResult.Transcription.AudioDuration.TotalSeconds:F2} s; transcripción: {importedResult.Transcription.Latency.TotalSeconds:F2} s.");
        Console.WriteLine($"Reporte guardado: {importedResult.SavedReportPath}");
        return 0;
    }

    Console.WriteLine("=== Meeting Assistant Harness ===");
    await pipeline.StartRecordingAsync(SessionSource.Harness);
    Console.WriteLine($"Grabando {durationSeconds} segundos...");
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
    MeetingPipelineResult result = await pipeline.StopRecordingAndProcessAsync();
    Console.WriteLine($"Audio: {result.Audio.AudioPath}");
    Console.WriteLine($"Loopback: {result.Audio.LoopbackDevice}");
    Console.WriteLine($"Mic: {result.Audio.MicrophoneDevice}");
    Console.WriteLine();
    Console.WriteLine("=== Transcript completo ===");
    Console.WriteLine(result.Transcription.Transcript);
    foreach (DiarizedUtterance utterance in result.Transcription.Utterances)
        Console.WriteLine($"Speaker {utterance.Speaker}: {utterance.Transcript}");
    Console.WriteLine($"Duracion: {result.Transcription.AudioDuration.TotalSeconds:F2} s; transcripcion: {result.Transcription.Latency.TotalSeconds:F2} s.");
    if (result.Report is not null)
    {
        PrintReport(result.Report);
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("=== Reporte generado ===");
        Console.WriteLine(result.ReportMarkdown);
    }
    Console.WriteLine($"Reporte guardado: {result.SavedReportPath}");
    return 0;
}
catch (MeetingReportParseException exception)
{
    Console.Error.WriteLine($"Error al interpretar el reporte: {exception.Message}");
    Console.Error.WriteLine("=== Output LLM que no pudo interpretarse ===");
    Console.Error.WriteLine(exception.RawOutput);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error en el pipeline: {exception.Message}");
    return 1;
}

static ILlmClient CreateLlmClient(IConfiguration configuration)
{
    string provider = ReadSetting(configuration, "Llm", "Provider") ?? "Gemini";
    return provider.ToLowerInvariant() switch
    {
        "gemini" => new GeminiLlmClient(ReadRequiredSetting(configuration, "Gemini", "ApiKey", "GEMINI_API_KEY")),
        "azurefoundry" => new AzureFoundryLlmClient(
            ReadRequiredSetting(configuration, "AzureFoundry", "Endpoint"),
            ReadRequiredSetting(configuration, "AzureFoundry", "Deployment"),
            ReadSetting(configuration, "AzureFoundry", "ApiKey")),
        _ => throw new InvalidOperationException($"Proveedor '{provider}' no soportado. Usa 'Gemini' o 'AzureFoundry'.")
    };
}

static string ReadRequiredSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null) =>
    ReadSetting(configuration, section, property, environmentVariable) ?? throw new InvalidOperationException(
        $"Falta configurar {section}:{property} en {appSettingsFileName} o la variable de entorno {environmentVariable}.");

static string? ReadSetting(IConfiguration configuration, string section, string property, string? environmentVariable = null)
{
    string? text = configuration[$"{section}:{property}"] ??
        (environmentVariable is null ? null : configuration[environmentVariable]);
    return string.IsNullOrWhiteSpace(text) || text.StartsWith("<", StringComparison.Ordinal) ? null : text;
}

static void PrintReport(MeetingReport report)
{
    Console.WriteLine();
    Console.WriteLine("=== Reporte de reunion ===");
    Console.WriteLine($"Resumen: {report.Summary}");
    PrintList("Insights", report.Insights);
    PrintList("Requirements", report.Requirements);
    PrintList("Indications", report.Indications);
    PrintList("Open questions", report.OpenQuestions);
    Console.WriteLine("Task list:");
    foreach (TaskItem task in report.TaskList)
        Console.WriteLine($"- [{task.Priority}] {task.Task}\n  Context: {task.Context}");

    if (report.Metadata is not null)
    {
        Console.WriteLine("Metadata:");
        Console.WriteLine($"Provider: {report.Metadata.LlmProvider}");
        Console.WriteLine($"Model: {report.Metadata.LlmModel}");
        Console.WriteLine($"Prompt version: {report.Metadata.PromptVersion}");
        Console.WriteLine($"Tokens: {report.Metadata.InputTokens} input, {report.Metadata.OutputTokens} output");
        Console.WriteLine($"Estimated cost: US${report.Metadata.EstimatedCostUsd:F6}");
    }
}

static void PrintList(string title, IReadOnlyList<string> values)
{
    Console.WriteLine($"{title}:");
    foreach (string value in values) Console.WriteLine($"- {value}");
}

/// <summary>
/// Corre el pipeline completo sobre un audio existente con un almacen de
/// historial apuntando a una base <b>temporal</b>, y comprueba que quedaron
/// registradas la sesion, el transcript y el reporte — ademas del .md en el
/// vault.
///
/// Existe porque el autotest de esquema prueba el store, pero no el cableado:
/// que el pipeline cree la sesion, guarde el transcript antes de extraer y
/// registre el reporte con su vault_path. Y porque hacerlo sobre un audio
/// existente no necesita microfono, que es justo lo que puede estar bloqueado
/// por el consentimiento de Windows.
///
/// Al final repite la corrida con la base apuntada a una ruta imposible, para
/// comprobar la regla que manda en este paso: <b>un fallo de base no puede
/// tumbar una grabacion</b>.
/// </summary>
static async Task<int> VerifyPipelineHistoryAsync(string audioPath, string? requestedPromptId)
{
    if (!File.Exists(audioPath))
    {
        Console.Error.WriteLine($"No existe el audio indicado: {audioPath}");
        return 1;
    }

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    IPromptCatalog catalog = new BuiltInPromptCatalog();
    ILlmReportExtractor extractor = new LlmReportExtractor(
        CreateLlmClient(configuration), new ConfigPricingCostEstimator(configuration), catalog);
    ITranscriptionClient transcription = new DeepgramTranscriptionClient(
        ReadRequiredSetting(configuration, "Deepgram", "ApiKey", "DEEPGRAM_API_KEY"));

    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-pipeline-{Guid.NewGuid():N}.db");
    var failuresSeen = new List<string>();

    try
    {
        var factory = new SqliteConnectionFactory(temporaryPath);
        new SqliteSchemaMigrator(factory).Migrate();
        IMeetingHistoryStore history = new SqliteMeetingHistoryStore(factory);

        Console.WriteLine($"=== Pipeline con historial ==={Environment.NewLine}{temporaryPath}{Environment.NewLine}");

        IMeetingPipeline pipeline = new MeetingPipeline(
            new AudioCaptureService(),
            transcription,
            extractor,
            new MarkdownReportStorage(configuration),
            Path.Combine(AppContext.BaseDirectory, "meeting-output"),
            history,
            (operation, exception) => failuresSeen.Add($"{operation}: {exception.Message}"));

        MeetingPipelineResult result = await pipeline.ProcessAudioFileAsync(audioPath);

        Check("el .md llego al vault", File.Exists(result.SavedReportPath));
        Check("no hubo fallos de historial", failuresSeen.Count == 0);
        if (failuresSeen.Count > 0) failuresSeen.ForEach(f => Console.WriteLine($"        {f}"));

        IReadOnlyList<SessionSummary> sessions = await history.ListSessionsAsync(10);
        Check("quedo registrada exactamente una sesion", sessions.Count == 1);
        Check("la sesion se marco como importada",
            sessions.Count == 1 && (await history.GetSessionAsync(sessions[0].SessionId))?.Source == SessionSource.Import);

        long sessionId = sessions[0].SessionId;
        TranscriptRecord? storedTranscript = await history.GetTranscriptAsync(sessionId);
        Check("el transcript quedo guardado", storedTranscript is not null);
        Check("el transcript guardado es el mismo que devolvio el pipeline",
            storedTranscript?.Text == result.Transcription.Transcript);

        IReadOnlyList<ReportRecord> reports = await history.GetReportsAsync(sessionId);
        Check("quedo registrado un reporte", reports.Count == 1);
        Check("el reporte apunta al .md del vault",
            reports.Count == 1 && reports[0].VaultPath == result.SavedReportPath);
        Check("el markdown guardado coincide con el generado",
            reports.Count == 1 && reports[0].Markdown == result.ReportMarkdown);
        Check("el costo del reporte quedo registrado",
            reports.Count == 1 && reports[0].CostUsd is not null);
        // structured_json solo para assignment-meeting; los demas prompts dan
        // Markdown suelto y tienen que dejarlo en null.
        bool isAssignmentPrompt = reports.Count == 1 && reports[0].PromptId == ReportExtractionPrompt.Id;
        Check($"structured_json coherente con el prompt ({(isAssignmentPrompt ? "assignment-meeting: presente" : "otro prompt: null")})",
            reports.Count == 1 && (isAssignmentPrompt ? reports[0].StructuredJson is not null : reports[0].StructuredJson is null));

        Check("la busqueda encuentra la reunion recien guardada",
            (await history.SearchTranscriptsAsync(
                result.Transcription.Transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])).Count >= 1);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El pipeline con historial reviento: {exception}");
        failures++;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }
    }

    // --- La regla que manda: una base rota no puede tumbar una grabacion -----
    Console.WriteLine($"{Environment.NewLine}--- Resiliencia: base apuntada a una ruta imposible ---");
    var brokenFailures = new List<string>();
    try
    {
        // Un directorio que no existe y no se puede crear.
        var brokenFactory = new SqliteConnectionFactory(@"\\?\Z:\no-existe\jamas\meetings.db");
        IMeetingHistoryStore brokenHistory = new SqliteMeetingHistoryStore(brokenFactory);

        IMeetingPipeline resilientPipeline = new MeetingPipeline(
            new AudioCaptureService(),
            transcription,
            extractor,
            new MarkdownReportStorage(configuration),
            Path.Combine(AppContext.BaseDirectory, "meeting-output"),
            brokenHistory,
            (operation, exception) => brokenFailures.Add(operation));

        MeetingPipelineResult resilientResult = await resilientPipeline.ProcessAudioFileAsync(audioPath);

        Check("la grabacion llego al vault pese a la base rota", File.Exists(resilientResult.SavedReportPath));
        Check("el transcript se produjo igual", resilientResult.Transcription.Transcript.Length > 0);
        Check("los fallos de base se registraron en vez de propagarse", brokenFailures.Count > 0);
        Console.WriteLine($"        operaciones que fallaron y se absorbieron: {string.Join(", ", brokenFailures.Distinct())}");
    }
    catch (Exception exception)
    {
        Check($"la grabacion llego al vault pese a la base rota (reviento: {exception.GetType().Name})", false);
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Prueba funcional del esquema sobre una base <b>temporal</b>, que se borra al
/// terminar. Nunca toca la base real.
///
/// Que las tablas existan no prueba que el esquema funcione. Lo que se ejercita
/// acá es justo lo que se rompe en silencio: los triggers que mantienen el
/// índice FTS5, el borrado en cascada de las foreign keys, y el tokenizador con
/// <c>remove_diacritics</c> — que importa porque el corpus es español mezclado
/// con inglés y nadie escribe los acentos al buscar.
/// </summary>
static int VerifyDatabaseSelfTest()
{
    string temporaryPath = Path.Combine(Path.GetTempPath(), $"ma-selftest-{Guid.NewGuid():N}.db");
    Console.WriteLine($"=== Autotest de esquema ==={Environment.NewLine}{temporaryPath}");

    int failures = 0;
    void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK " : "MAL")}] {description}");
        if (!passed) failures++;
    }

    try
    {
        var factory = new SqliteConnectionFactory(temporaryPath);
        string migrationResult = new SqliteSchemaMigrator(factory).Migrate();
        Console.WriteLine($"Migracion: {migrationResult}{Environment.NewLine}");

        using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

        long Scalar(string sql)
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }

        void Execute(string sql)
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Execute(
            """
            insert into session(id, started_at_utc, source) values (1, '2026-08-27T09:00:00Z', 'hotkey');
            insert into transcript(session_id, text, provider, model, cost_micro_usd, created_at_utc)
                values (1, 'Hablamos de la migracion del stored procedure y la validacion de sesión.',
                        'Deepgram', 'nova-3', 434, '2026-08-27T09:05:00Z');
            insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                values (1, 'assignment-meeting', 'v1', '# Reporte', 762, '2026-08-27T09:06:00Z');
            insert into report(session_id, prompt_id, prompt_version, markdown, cost_micro_usd, created_at_utc)
                values (1, 'feature-handoff', 'v1', '# Handoff', 900, '2026-08-27T09:07:00Z');
            """);

        Check("una sesion admite varios reportes", Scalar("select count(*) from report where session_id = 1;") == 2);
        Check("el trigger de insert alimento el indice FTS5",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 1);
        Check("busqueda sin acento encuentra texto acentuado ('sesion' -> 'sesión')",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'sesion';") == 1);
        Check("busqueda con acento tambien encuentra ('sesión')",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'sesión';") == 1);
        Check("una palabra ausente no da falsos positivos",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'zanahoria';") == 0);

        Execute("update transcript set text = 'Ahora hablamos de facturacion electronica.' where session_id = 1;");
        Check("el trigger de update reindexo (lo viejo ya no aparece)",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'stored';") == 0);
        Check("el trigger de update reindexo (lo nuevo si aparece)",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 1);

        Check("el costo entero sobrevive el viaje sin deriva",
            Scalar("select sum(cost_micro_usd) from report;") == 1662L);

        Execute("delete from session where id = 1;");
        Check("borrar la sesion arrastra el transcript (cascade)", Scalar("select count(*) from transcript;") == 0);
        Check("borrar la sesion arrastra los reportes (cascade)", Scalar("select count(*) from report;") == 0);
        Check("el trigger de delete limpio el indice FTS5",
            Scalar("select count(*) from transcript_fts where transcript_fts match 'facturacion';") == 0);

        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
            try
            {
                command.ExecuteNonQuery();
                Check("el indice FTS5 queda consistente al final", true);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                Check("el indice FTS5 queda consistente al final", false);
            }
        }

        // --- Ahora lo mismo, pero a traves de las interfaces de Core ---------
        // El SQL de arriba prueba el esquema; esto prueba los adaptadores, que
        // es donde viven las conversiones (dinero a micro-dolares, fechas a UTC)
        // y el saneado de la consulta FTS5.
        Console.WriteLine($"{Environment.NewLine}--- Store, via IMeetingHistoryStore ---");

        IMeetingHistoryStore store = new SqliteMeetingHistoryStore(factory);
        var startedAt = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.FromHours(-7));

        long sessionId = store.CreateSessionAsync(startedAt, SessionSource.Hotkey).GetAwaiter().GetResult();
        store.CompleteSessionAsync(sessionId, startedAt.AddMinutes(12), @"C:\audio\x.wav", TimeSpan.FromMinutes(12))
            .GetAwaiter().GetResult();
        store.SaveTranscriptAsync(new TranscriptRecord(
            sessionId, "Revisamos la integracion con el módulo de facturación.",
            "Deepgram", "nova-3", 0.001234m, startedAt.AddMinutes(13))).GetAwaiter().GetResult();
        store.SaveReportAsync(new NewReport(
            sessionId, "assignment-meeting", "v1", "# Reporte", null,
            "Azure AI Foundry", "DeepSeek-V4-Flash", 890, 520, 0.000434m,
            @"C:\vault\r.md", startedAt.AddMinutes(14))).GetAwaiter().GetResult();

        SessionRecord? roundTripped = store.GetSessionAsync(sessionId).GetAwaiter().GetResult();
        Check("la sesion vuelve con su id", roundTripped?.Id == sessionId);
        // El instante tiene que ser el mismo aunque se haya guardado en UTC y el
        // original viniera con offset -07:00. Es la garantia que hace que
        // ordenar por fecha no dependa del huso de quien grabo.
        Check("el instante sobrevive la ida y vuelta a UTC", roundTripped?.StartedAtUtc == startedAt);
        Check("la duracion sobrevive", roundTripped?.Duration == TimeSpan.FromMinutes(12));

        TranscriptRecord? storedTranscript = store.GetTranscriptAsync(sessionId).GetAwaiter().GetResult();
        Check("el costo decimal del transcript vuelve exacto", storedTranscript?.CostUsd == 0.001234m);

        IReadOnlyList<ReportRecord> storedReports = store.GetReportsAsync(sessionId).GetAwaiter().GetResult();
        Check("el costo decimal del reporte vuelve exacto", storedReports.Count == 1 && storedReports[0].CostUsd == 0.000434m);
        Check("los tokens vuelven", storedReports.Count == 1 && storedReports[0].InputTokens == 890);

        IReadOnlyList<SessionSummary> listed = store.ListSessionsAsync(10).GetAwaiter().GetResult();
        Check("el listado trae la sesion con su conteo de reportes",
            listed.Count == 1 && listed[0].ReportCount == 1);
        Check("el costo de la sesion suma transcripcion y reporte",
            listed.Count == 1 && listed[0].TotalCostUsd == 0.001668m);

        IReadOnlyList<TranscriptSearchHit> hits =
            store.SearchTranscriptsAsync("facturacion").GetAwaiter().GetResult();
        Check("la busqueda por interfaz encuentra sin acentos", hits.Count == 1);
        Check("el resultado trae un fragmento, no solo la fecha",
            hits.Count == 1 && hits[0].Snippet.Length > 0);

        // Lo que mas importa de todo el saneado: escribiendo en una caja de
        // busqueda uno pasa por estados invalidos de sintaxis FTS5, y ninguno
        // puede llegar al usuario como un error de SQL.
        bool survivedGarbage = true;
        foreach (string hostile in new[] { "\"", "AND", "NEAR(", "*", "a OR", "(((", "'" })
        {
            try { store.SearchTranscriptsAsync(hostile).GetAwaiter().GetResult(); }
            catch (Exception) { survivedGarbage = false; }
        }
        Check("una consulta a medias o con operadores sueltos no lanza", survivedGarbage);

        CostSummary costs = store.GetCostSummaryAsync().GetAwaiter().GetResult();
        Check("el resumen de costo acumulado cuadra", costs.TotalCostUsd == 0.001668m && costs.ReportCount == 1);

        IReadOnlyList<PromptUsageSummary> usage = store.GetPromptUsageAsync().GetAwaiter().GetResult();
        Check("el uso por prompt agrupa por id y version",
            usage.Count == 1 && usage[0].PromptId == "assignment-meeting" && usage[0].ReportCount == 1);

        // --- Ajustes y cifrado ---------------------------------------------
        Console.WriteLine($"{Environment.NewLine}--- Ajustes, via ISettingsStore ---");

        ISettingsStore settings = new SqliteSettingsStore(factory, new DpapiSecretProtector());
        settings.SetAsync("Storage:VaultPath", @"C:\vault").GetAwaiter().GetResult();
        settings.SetAsync("Deepgram:ApiKey", "clave-secreta-de-prueba", isSecret: true).GetAwaiter().GetResult();

        Check("un ajuste normal vuelve tal cual",
            settings.GetAsync("Storage:VaultPath").GetAwaiter().GetResult() == @"C:\vault");
        Check("un secreto vuelve descifrado",
            settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() == "clave-secreta-de-prueba");

        // La comprobacion que hace que valga la pena: en disco NO puede estar en
        // claro. Sin esto, mover las claves a la base no habria mejorado nada.
        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select value from setting where key = 'Deepgram:ApiKey';";
            string onDisk = command.ExecuteScalar()?.ToString() ?? string.Empty;
            Check("el secreto esta cifrado en disco, no en claro",
                onDisk.Length > 0 && !onDisk.Contains("clave-secreta-de-prueba", StringComparison.Ordinal));
        }

        settings.SetAsync("Deepgram:ApiKey", "   ").GetAwaiter().GetResult();
        Check("guardar vacio borra el override (se vuelve al valor empaquetado)",
            settings.GetAsync("Deepgram:ApiKey").GetAwaiter().GetResult() is null);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"El autotest reviento: {exception}");
        failures++;
    }
    finally
    {
        // El pool mantiene el archivo abierto; sin esto el borrado falla en
        // Windows y quedan bases de prueba tiradas en %TEMP%.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string leftover in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); } catch { /* best effort */ }
        }
    }

    Console.WriteLine($"{Environment.NewLine}{(failures == 0 ? "Todo OK." : $"{failures} comprobacion(es) fallaron.")}");
    return failures == 0 ? 0 : 1;
}

/// <summary>
/// Inspecciona la base local de reuniones: versión de esquema, objetos creados y
/// filas por tabla. Existe para poder comprobar contra la máquina lo que una
/// migración dice haber hecho, sin depender de tener instalado un cliente de
/// SQLite. Mismo criterio que --verify-render.
///
/// Apunta a la misma ruta que usa la app instalada
/// (%LOCALAPPDATA%\MeetingAssistant\meetings.db), así que lee la base de verdad,
/// no una copia.
/// </summary>
static int VerifyDatabase()
{
    string databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingAssistant",
        "meetings.db");

    Console.WriteLine($"=== Base de reuniones ==={Environment.NewLine}{databasePath}");
    if (!File.Exists(databasePath))
    {
        Console.Error.WriteLine("No existe todavia. La crea el primer arranque de la app.");
        return 1;
    }

    Console.WriteLine($"Tamano: {new FileInfo(databasePath).Length:N0} bytes");

    var factory = new SqliteConnectionFactory(databasePath);
    using Microsoft.Data.Sqlite.SqliteConnection connection = factory.Open();

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "pragma user_version;";
        Console.WriteLine($"Version de esquema: v{command.ExecuteScalar()} (el codigo espera v{SqliteSchemaMigrator.LatestVersion})");
    }

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText =
            "select type, name from sqlite_master where name not like 'sqlite_%' order by type, name;";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}=== Objetos ===");
        while (reader.Read()) Console.WriteLine($"  {reader.GetString(0),-7} {reader.GetString(1)}");
    }

    Console.WriteLine($"{Environment.NewLine}=== Filas ===");
    foreach (string table in new[] { "session", "transcript", "report", "setting" })
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table};";
        Console.WriteLine($"  {table,-12} {command.ExecuteScalar()}");
    }

    // Las ultimas sesiones, con su origen. Es lo primero que uno quiere ver al
    // mirar la base de verdad, y lo que dice si la columna source se esta
    // rellenando distinto segun el camino o siempre igual.
    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText =
            """
            select s.id, s.started_at_utc, s.source, s.duration_seconds,
                   (select count(*) from transcript t where t.session_id = s.id),
                   (select count(*) from report r where r.session_id = s.id)
              from session s order by s.started_at_utc desc limit 10;
            """;
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}=== Ultimas sesiones ===");
        Console.WriteLine($"  {"id",-4} {"inicio (UTC)",-26} {"origen",-9} {"dur(s)",-8} {"trans",-6} reportes");
        while (reader.Read())
        {
            Console.WriteLine(
                $"  {reader.GetInt64(0),-4} {reader.GetString(1),-26} {reader.GetString(2),-9} " +
                $"{(reader.IsDBNull(3) ? "-" : reader.GetDouble(3).ToString("F1")),-8} " +
                $"{reader.GetInt32(4),-6} {reader.GetInt32(5)}");
        }
    }

    // La integridad referencial y el indice FTS son justo lo que se rompe en
    // silencio: comprobarlos a mano es la unica forma de saber que los triggers
    // y las foreign keys quedaron como se creian.
    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "pragma foreign_key_check;";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        Console.WriteLine($"{Environment.NewLine}Integridad referencial: {(reader.Read() ? "FALLOS" : "sin violaciones")}");
    }

    using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "insert into transcript_fts(transcript_fts) values('integrity-check');";
        try
        {
            command.ExecuteNonQuery();
            Console.WriteLine("Indice FTS5: consistente");
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            Console.WriteLine($"Indice FTS5: INCONSISTENTE — {exception.Message}");
            return 1;
        }
    }

    return 0;
}

static int VerifyCatalogAndRender()
{
    IPromptCatalog catalog = new BuiltInPromptCatalog();
    Console.WriteLine("=== Catalogo de prompts ===");
    foreach (PromptDefinition prompt in catalog.GetAll())
    {
        Console.WriteLine($"- {prompt.Id} @{prompt.Version}: {prompt.DisplayName} ({prompt.OutputKind})");
    }

    PromptDefinition functional = catalog.GetById("functional-spec");
    Console.WriteLine();
    Console.WriteLine($"=== Prompt functional-spec @{functional.Version} ===");
    Console.WriteLine(functional.SystemPrompt);

    string[] requiredFragments =
    [
        "same language as the transcript",
        "Executive summary",
        "Identified entities and states",
        "Text-based flow diagram",
        "Business rules and conditions",
        "Ambiguous or pending points to confirm",
        "Agreed actions/decisions"
    ];

    string[] missing = requiredFragments
        .Where(fragment => !functional.SystemPrompt.Contains(fragment, StringComparison.Ordinal))
        .ToArray();

    if (missing.Length > 0)
    {
        Console.Error.WriteLine("El prompt functional-spec no contiene: " + string.Join(", ", missing));
        return 1;
    }

    if (functional.SystemPrompt.Contains("CFS", StringComparison.Ordinal) ||
        functional.Version != FunctionalSpecPrompt.Version)
    {
        Console.Error.WriteLine("El prompt functional-spec no es la versión esperada o todavía menciona un dominio ajeno.");
        return 1;
    }

    Console.WriteLine("OK: catalogo y prompt de especificación funcional.");
    return 0;
}
